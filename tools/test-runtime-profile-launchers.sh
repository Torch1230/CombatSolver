#!/usr/bin/env bash
# No-game contract checks for the opt-in process launcher. Requires cc, python3, jq, and rg.
set -Eeuo pipefail
script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
fixture_root="$(mktemp -d "${TMPDIR:-/tmp}/combatsolver runtime profile.XXXXXX")"
fixture_pid=""
cleanup() {
    if [[ -n $fixture_pid ]]; then
        kill "$fixture_pid" 2>/dev/null || true
        wait "$fixture_pid" 2>/dev/null || true
    fi
    rm -rf -- "$fixture_root"
}
trap cleanup EXIT
fail() { echo "runtime-profile-launchers: $*" >&2; exit 1; }
cat >"$fixture_root/fixture.c" <<'EOF'
#include <stdio.h>
#include <stdlib.h>
#include <unistd.h>
int main(int argc, char **argv) {
    FILE *output = fopen(getenv("RUNTIME_PROFILE_TEST_OUTPUT"), "wb");
    if (!output) return 90;
    const char *keys[] = {"DOTNET_gcServer", "COMPlus_gcServer", "COMBATSOLVER_RUNTIME_PROFILE"};
    for (int i = 0; i < 3; ++i) {
        const char *value = getenv(keys[i]);
        fprintf(output, "%s%c", value ? value : "", 0);
    }
    char directory[4096];
    if (!getcwd(directory, sizeof directory)) return 91;
    fprintf(output, "%s%c", directory, 0);
    for (int i = 1; i < argc; ++i) fprintf(output, "%s%c", argv[i], 0);
    fclose(output);
    if (getenv("RUNTIME_PROFILE_TEST_HOLD")) for (;;) sleep(1);
    const char *exit_code = getenv("RUNTIME_PROFILE_TEST_EXIT");
    return exit_code ? atoi(exit_code) : 0;
}
EOF
cc -O0 -o "$fixture_root/mock game" "$fixture_root/fixture.c"
bash -n "$script_dir/start-server-gc.sh" "$script_dir/run-unattended-test.sh"
export DOTNET_gcServer=0 COMPlus_gcServer=0 COMBATSOLVER_RUNTIME_PROFILE=parent-profile
export RUNTIME_PROFILE_TEST_OUTPUT="$fixture_root/result.bin"
arguments=('with spaces' '' 'embedded"quote' 'literal$(not-a-command)' 'semi;colon' '--option=value' 'trailing\')
bash "$script_dir/start-server-gc.sh" "$fixture_root/mock game" "${arguments[@]}" >"$fixture_root/start.log"
python3 - "$RUNTIME_PROFILE_TEST_OUTPUT" "$fixture_root" "${arguments[@]}" <<'PY'
import pathlib,sys
actual=pathlib.Path(sys.argv[1]).read_bytes().decode().split('\0')[:-1]
assert actual == ['1','1','server-generational',sys.argv[2],*sys.argv[3:]], actual
PY
[[ $DOTNET_gcServer == 0 && $COMPlus_gcServer == 0 && $COMBATSOLVER_RUNTIME_PROFILE == parent-profile ]] \
    || fail 'child launch mutated parent environment'
exit_code=0
RUNTIME_PROFILE_TEST_EXIT=37 bash "$script_dir/start-server-gc.sh" "$fixture_root/mock game" \
    >"$fixture_root/failure.log" 2>&1 || exit_code=$?
[[ $exit_code == 37 ]] || fail "child exit code was lost: $exit_code"

RUNTIME_PROFILE_TEST_OUTPUT="$fixture_root/held.bin" RUNTIME_PROFILE_TEST_HOLD=1 \
    "$fixture_root/mock game" &
fixture_pid=$!
for ((attempt = 0; attempt < 100; attempt++)); do
    [[ -f $fixture_root/held.bin ]] && break
    sleep 0.01
done
[[ -f $fixture_root/held.bin ]] || fail 'fixture did not start'
exit_code=0
bash "$script_dir/start-server-gc.sh" "$fixture_root/mock game" >"$fixture_root/rejected.log" 2>&1 || exit_code=$?
[[ $exit_code == 2 ]] || fail "existing process was not rejected: $exit_code"
kill -0 "$fixture_pid" || fail 'launcher terminated the existing process'
rg -q 'already running' "$fixture_root/rejected.log" || fail 'missing rejection reason'

exit_code=0
bash "$script_dir/run-unattended-test.sh" --runtime-profile invalid-profile \
    >"$fixture_root/invalid.log" 2>&1 || exit_code=$?
[[ $exit_code == 2 ]] || fail 'unattended accepted an unknown profile'
rg -q 'runtime-profile.*must be one of|runtime-profile.*invalid|runtime-profile.*expected' "$fixture_root/invalid.log" \
    || { cat "$fixture_root/invalid.log" >&2; fail 'unknown profile failed outside option validation'; }

# Exercise the real environment setup and owned-marker mismatch branch without
# entering game discovery/admission. The stop operation alone is a recording stub.
python3 - "$script_dir/run-unattended-test.sh" "$fixture_root" <<'PY'
import pathlib,sys
source=pathlib.Path(sys.argv[1]).read_text()
root=pathlib.Path(sys.argv[2])
start=source.index('runtime_profile="${option_value[runtime-profile]}"')
end=source.index('\n\nprocess_pid=""',start)
(root/'profile.sh').write_text(source[start:end]+'\n')
start=source.index('        if [[ "$marker_dll_sha256" != "$combat_solver_dll_sha256"')
end=source.index('\n        elif [[ -f "$ready_path"',start)
(root/'reuse.sh').write_text(source[start:end]+'\n        fi\n')
PY
declare -A option_value=()
for selected in default server-generational; do
    option_value[runtime-profile]=$selected
    source "$fixture_root/profile.sh"
    expected_gc=0; expected_profile=''
    if [[ $selected == server-generational ]]; then expected_gc=1; expected_profile=$selected; fi
    jq -e --arg gc "$expected_gc" --arg profile "$expected_profile" \
        '.DOTNET_gcServer == $gc and .COMPlus_gcServer == $gc and .COMBATSOLVER_RUNTIME_PROFILE == $profile' \
        <<<"$runtime_environment_key" >/dev/null || fail 'unattended child environment differs'
done
stop_calls=0
stop_test_process_and_remove_dependency() {
    [[ $1 == 123456 && $2 == fixture-birth ]] || fail 'restart lost owned process identity'
    stop_calls=$((stop_calls + 1))
}
marker_dll_sha256=same; combat_solver_dll_sha256=same
marker_manifest_sha256=same; combat_solver_manifest_sha256=same
marker_artifact_id=same; artifact_id=same
HR_MODE=exclusive; HR_MEMORY=4096; HR_CPU=1
process_marker_path="$fixture_root/process.json"
for marker_case in same changed legacy; do
    process_pid=123456; process_identity_start_time=fixture-birth; stop_calls=0
    key=$runtime_environment_key
    [[ $marker_case != changed ]] || key=another-runtime
    jq -cn --arg key "$key" '{runtimeEnvironmentKey:$key,executionMode:"exclusive",memoryMiB:4096,cpu:1}' \
        >"$process_marker_path"
    if [[ $marker_case == legacy ]]; then
        jq 'del(.runtimeEnvironmentKey)' "$process_marker_path" >"$fixture_root/legacy.json"
        mv -- "$fixture_root/legacy.json" "$process_marker_path"
    fi
    source "$fixture_root/reuse.sh" 2>"$fixture_root/reuse.log"
    expected_stops=1; [[ $marker_case != same ]] || expected_stops=0
    [[ $stop_calls == "$expected_stops" ]] || fail "wrong restart decision for $marker_case"
done
[[ $DOTNET_gcServer == 0 && $COMPlus_gcServer == 0 && $COMBATSOLVER_RUNTIME_PROFILE == parent-profile ]] \
    || fail 'unattended environment setup mutated parent variables'
echo 'RUNTIME_PROFILE_LAUNCHERS_SH_OK arguments/environment/exit-code/existing-process/invalid-profile/marker-reuse'
