#!/usr/bin/env bash
set -Eeuo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$script_dir/.." && pwd)"
cd -- "$repo_root"

die() {
    echo "run-unattended-test.sh: $*" >&2
    exit 2
}

runtime_error() {
    echo "run-unattended-test.sh: $*" >&2
    exit 1
}

declare -A option_value=()
declare -A option_kind=()
declare -A option_wire=()
declare -A option_choices=()
declare -a option_order=()
declare -a modifier_ids=()
declare -a additional_monster_ids=()

add_option() {
    local name="$1" default_value="$2" kind="$3" wire_type="$4" choices="${5-}"
    option_order+=("$name")
    option_value["$name"]="$default_value"
    option_kind["$name"]="$kind"
    option_wire["$name"]="$wire_type"
    option_choices["$name"]="$choices"
}

host_data_home="${XDG_DATA_HOME:-${HOME:?HOME is required}/.local/share}"
if [[ -n "${COMBATSOLVER_STEAM_ROOT:-}" ]]; then
    steam_root="$COMBATSOLVER_STEAM_ROOT"
elif [[ -d "$HOME/.local/share/Steam" ]]; then
    steam_root="$HOME/.local/share/Steam"
elif [[ -d "$HOME/.steam/steam" ]]; then
    steam_root="$HOME/.steam/steam"
else
    steam_root="$HOME/.local/share/Steam"
fi
add_option scenario-id "SMOKE-001" string raw_string
add_option character-id "IRONCLAD" string raw_string
add_option seed "COMBATSOLVER" string raw_string
add_option encounter-id "FUZZY_WURM_CRAWLER_WEAK" string raw_string
add_option sts2-game-root "$steam_root/steamapps/common/Slay the Spire 2" string none
add_option ritsu-workshop-root "$steam_root/steamapps/workshop/content/2868840/3747602295" string none
add_option run-snapshot-path "" string none
add_option replay-state-path "" string none
add_option progress-snapshot-path "" string none
add_option ascension 0 int raw_int
add_option act-index-for-test 0 int raw_int
add_option mark-encounter-as-second-boss-for-test 0 switch bool
add_option enemy-current-hp 1 int raw_int
add_option initial-enemy-current-hps-json "" string none
add_option initial-player-hp -1 int positive_int
add_option initial-player-max-hp -1 int positive_int
for name in initial-player-block initial-player-energy initial-player-stars initial-round-number initial-player-turn-number; do
    add_option "$name" -1 int nonnegative_int
done
add_option initial-enemy-state-logs-json "" string none
add_option reload-run-rng-after-state-injection 0 switch bool

add_option card-id "STRIKE_IRONCLAD" string none
add_option power-id "" string none
add_option powers-json "" string none
add_option powers-path "" string none
add_option power-amount 1 int none
add_option power-target "Enemy" string none "Player|Enemy"
add_option monster-move-id "" string none
add_option monster-id "" string none
add_option expected-player-hp-loss -1 int none
add_option expected-enemy-block-gain -1 int none
add_option expected-player-powers-json "{}" string none
add_option expected-enemy-powers-json "{}" string none
for name in \
    monster-move-checks-json monster-move-checks-path \
    orbs-json orb-checks-json orb-checks-path \
    relics-json relics-path combat-relics-json combat-relics-path \
    cards-json cards-path run-cards-json run-cards-path \
    potion-check-json potion-check-path potion-checks-json potion-checks-path \
    potion-id potions-json potions-path initial-enemy-move-ids-json; do
    add_option "$name" "" string none
done
add_option modifier-id "" multi none
add_option additional-monster-id "" multi none

add_option expected-finished-turn 0 int positive_int
add_option expected-finished-turn-at-most 0 int positive_int
add_option expected-finished-player-hp-at-least -1 int nonnegative_int
for name in \
    clear-player-hand clear-player-piles clear-run-deck clear-all-powers \
    verify-prediction-failure-boundaries verify-search-policy-snapshot \
    verify-controller-session-lifecycle verify-fork-boundaries \
    verify-combat-root-snapshot verify-base-lib-card-modifier-boundary \
    stop-after-combat-root-snapshot-assertion verify-incremental-search \
    force-short-search-only measure-search-phases hold-after-initial-search; do
    add_option "$name" 0 switch bool
done
add_option short-search-budget-override-milliseconds -1 int positive_int
add_option deep-search-budget-override-milliseconds -1 int positive_int
add_option search-max-degree-of-parallelism-for-test -1 int positive_int
add_option expected-initial-search-phase "" string optional_string "Short|Deep"
add_option expected-initial-deep-search-triggered -1 int tri_bool
add_option expected-initial-deep-search-improved-result -1 int tri_bool
for name in \
    expected-initial-total-elapsed-milliseconds-at-most \
    expected-initial-total-gc-pause-milliseconds-at-most \
    expected-initial-max-gc-pause-milliseconds-at-most \
    expected-initial-max-main-thread-frame-gap-milliseconds-at-most; do
    add_option "$name" -1 number nonnegative_number
done
for name in \
    expected-initial-total-allocated-bytes-at-most \
    expected-initial-gen2-collections-at-most \
    expected-initial-main-thread-frames-over50-milliseconds-at-most \
    expected-initial-main-thread-frames-over100-milliseconds-at-most \
    expected-initial-transition-cache-hits-at-least \
    expected-initial-repeatable-no-progress-branches-pruned-at-least \
    expected-initial-choice-branches-evaluated-at-least \
    expected-initial-executable-action-count-at-least \
    expected-initial-sold-hp expected-initial-sold-hp-at-most \
    expected-initial-sold-hp-branches-pruned-at-least \
    expected-initial-action-admission-representatives-protected-at-least \
    expected-initial-hp-investment-branches-protected-at-least \
    expected-initial-potion-count expected-initial-potion-hp-saved-at-least \
    expected-initial-potion-branches-rejected-at-least \
    expected-initial-outstanding-stolen-resource \
    expected-initial-searched-turns-at-least expected-initial-shuffles-crossed-at-least \
    expected-initial-unmirrored-count expected-initial-hp-lost-at-most \
    expected-initial-projected-battle-hp-lost \
    expected-initial-projected-battle-hp-lost-at-most \
    expected-initial-long-term-resource-value-at-least \
    expected-initial-final-max-hp expected-initial-max-block-at-least \
    expected-initial-actual-block-at-least expected-initial-action-replay-count; do
    add_option "$name" -1 int nonnegative_int
done
add_option expected-initial-theft-policy "" string optional_string "PreserveResources|LetEscape"
for name in \
    expected-initial-action-card-id expected-initial-absent-action-card-id \
    expected-initial-first-action-card-id expected-initial-first-action-potion-id \
    expected-initial-action-title; do
    add_option "$name" "" string optional_string
done
add_option expected-initial-only-death-routes-found -1 int tri_bool
add_option expected-initial-combat-ended-turn 0 int positive_int
add_option expected-initial-death-turn 0 int positive_int
add_option expected-initial-death-turn-at-least 0 int positive_int
add_option expected-initial-final-enemy-hp-at-most -1 int nonnegative_int
add_option expected-initial-act-ending-boss -1 int tri_bool
add_option expected-initial-planned-choice-card-id "" string optional_string
add_option expected-initial-turn-start-choice-turn 0 int positive_int
for name in \
    expected-initial-turn-start-choice-source-id expected-initial-turn-start-choice-card-id \
    expected-initial-turn-start-choice-state-contains expected-initial-turn-start-choice-state-excludes; do
    add_option "$name" "" string optional_string
done
add_option expected-initial-setup-choice-count-at-least -1 int nonnegative_int
add_option expected-initial-setup-choice-source-id "" string optional_string
add_option expected-initial-setup-choice-text-starts-with "" string optional_string
for name in \
    verify-turn-setup-manual-recalculate verify-turn-setup-manual-refresh \
    verify-turn-setup-controls-during-initial-search verify-turn-setup-scene-exit-cancellation \
    stop-after-initial-setup-assertion stop-after-initial-solver-result-assertion \
    expected-full-auto-paused-at-death-turn \
    expected-full-auto-paused-after-worse-recalculation \
    expected-full-auto-paused-at-live-risk enable-stop-on-worse-recalculation-for-test; do
    add_option "$name" 0 switch bool
done
add_option expected-initial-relic-effect-id "" string optional_string
add_option expected-initial-relic-effect-summary "" string optional_string
add_option expected-reused-turn 0 int positive_int
add_option expected-reused-projected-battle-hp-lost -1 int nonnegative_int
add_option expected-unexpected-replans-at-most -1 int nonnegative_int
add_option stop-after-expected-reuse 0 switch bool
for name in expected-played-card-id expected-used-potion-id expected-observed-player-power-id expected-native-choice-owner-prefix; do
    add_option "$name" "" string optional_string
done
add_option expected-native-choice-surface "" string optional_string "ChooseCard|SimpleGrid|CombatPile|Hand|HandUpgrade"
add_option expected-native-choice-visible-at-least -1 int nonnegative_int
add_option expected-native-choice-search-started-at-most -1 int nonnegative_int
add_option stop-after-expected-player-power 0 switch bool
add_option expected-player-death 0 switch bool

# Headless tests intentionally default to the fastest game-speed override.
add_option headless-fast-mode-for-test "Instant" string optional_string "FollowGame|Normal|Fast|Instant"
add_option deployment-fast-mode-for-test "" string optional_string "FollowGame|Normal|Fast|Instant"
add_option performance-preset-for-test "" string optional_string "Low|Medium|High|VeryHigh|Custom"
add_option potion-policy-for-test "" string optional_string "Disabled|Smart|RequireAtLeastOne"
add_option theft-policy-for-test "" string optional_string "PreserveResources|LetEscape"
add_option no-gc-region-budget-gigabytes-for-test -1 number positive_number
add_option deployment-inter-action-delay-seconds-for-test -1 number nonnegative_number
for name in assert-deployment-speed-restored export-bug-report-after-setup export-bug-report-after-combat; do
    add_option "$name" 0 switch bool
done
add_option enable-detailed-diagnostic-logs-for-test -1 int tri_bool
add_option manual-end-turn-after-initial-search 0 switch bool
add_option single-step-after-initial-search 0 switch bool
add_option single-step-resume-mode-for-test "" string optional_string "ExecuteCurrentTurn|FullAuto"
add_option expected-turn-setup-to-deployment-delay-milliseconds-at-least -1 int nonnegative_int
add_option enable-full-auto-after-manual-end-turn 0 switch bool
add_option expected-manual-divergences-at-least -1 int nonnegative_int
add_option expected-unexpected-replans-at-least -1 int nonnegative_int
for name in \
    stop-after-expected-unexpected-replan expected-unexpected-replan-warning \
    export-bug-report-after-unexpected-replan; do
    add_option "$name" 0 switch bool
done
add_option expected-bug-report-control-mode "" string optional_string "solver_only|manual_plus_solver"
add_option expected-no-gc-region-rollovers-at-least -1 int nonnegative_int
add_option inject-player-hp-loss-before-auto-search-turn 0 int positive_int
add_option inject-player-hp-loss-amount 0 int raw_int
add_option clear-player-block-before-end-turn-for-test 0 int positive_int
add_option timeout-seconds 150 int raw_int
add_option keep-game-open 0 switch none
add_option exit-on-complete 0 switch bool

print_help() {
    cat <<'EOF'
Usage: tools/run-unattended-test.sh [options]

Runs one CombatSolver unattended request in an isolated native-Linux headless
Slay the Spire 2 process. A successful process is reused unless
--exit-on-complete is supplied. --headless-fast-mode-for-test defaults to
Instant; pass FollowGame, Normal, Fast, or Instant to override it.

Options use GNU kebab-case. Switches take no value; --modifier-id and
--additional-monster-id may be repeated. Scalar values accept either
--option VALUE or --option=VALUE.

Accepted options:
EOF
    local name kind default_value choices
    for name in "${option_order[@]}"; do
        kind="${option_kind[$name]}"
        default_value="${option_value[$name]}"
        choices="${option_choices[$name]}"
        if [[ "$kind" == switch ]]; then
            printf '  --%s\n' "$name"
        elif [[ "$kind" == multi ]]; then
            printf '  --%s VALUE  (repeatable)\n' "$name"
        elif [[ -n "$choices" ]]; then
            printf '  --%s VALUE  {%s}' "$name" "${choices//|/,}"
            [[ -n "$default_value" ]] && printf ' [default: %s]' "$default_value"
            printf '\n'
        else
            printf '  --%s VALUE' "$name"
            [[ -n "$default_value" ]] && printf ' [default: %s]' "$default_value"
            printf '\n'
        fi
    done
    cat <<'EOF'
  -h, --help
      Show this help.
  --list-options
      Print only accepted long-option names, one per line.

Environment:
  COMBATSOLVER_STEAM_ROOT    Override the native Linux Steam root.
  COMBATSOLVER_HEADLESS_ROOT  Override the isolated Linux runtime root.
EOF
}

list_options() {
    local name
    for name in "${option_order[@]}"; do
        printf -- '--%s\n' "$name"
    done
}

while (($# > 0)); do
    argument="$1"
    case "$argument" in
        -h|--help) print_help; exit 0 ;;
        --list-options) list_options; exit 0 ;;
    esac
    [[ "$argument" == --* ]] || die "unexpected positional argument: $argument"
    inline_value=0
    if [[ "$argument" == *=* ]]; then
        name="${argument%%=*}"
        name="${name#--}"
        value="${argument#*=}"
        inline_value=1
    else
        name="${argument#--}"
        value=""
    fi
    [[ "$name" =~ ^[a-z0-9][a-z0-9-]*$ ]] || die "invalid option name: $argument"
    [[ ${option_kind[$name]+present} ]] || die "unknown option: --$name"
    kind="${option_kind[$name]}"
    case "$kind" in
        switch)
            ((inline_value == 0)) || die "--$name is a switch and takes no value"
            option_value["$name"]=1
            shift
            ;;
        string|int|number|multi)
            if ((inline_value == 0)); then
                (($# >= 2)) || die "missing value for --$name"
                value="$2"
                shift 2
            else
                shift
            fi
            if [[ "$kind" == multi ]]; then
                case "$name" in
                    modifier-id) modifier_ids+=("$value") ;;
                    additional-monster-id) additional_monster_ids+=("$value") ;;
                    *) die "internal error: unsupported repeatable option --$name" ;;
                esac
            else
                option_value["$name"]="$value"
            fi
            ;;
        *) die "internal error: unsupported option type for --$name" ;;
    esac
done

is_integer() { [[ "$1" =~ ^-?[0-9]+$ ]]; }
is_number() { [[ "$1" =~ ^-?(([0-9]+([.][0-9]*)?)|([.][0-9]+))([eE][+-]?[0-9]+)?$ ]]; }
is_blank() { [[ "$1" =~ ^[[:space:]]*$ ]]; }

for name in "${option_order[@]}"; do
    kind="${option_kind[$name]}"
    value="${option_value[$name]}"
    case "$kind" in
        int) is_integer "$value" || die "--$name requires an integer: $value" ;;
        number) is_number "$value" || die "--$name requires a number: $value" ;;
    esac
    choices="${option_choices[$name]}"
    if [[ -n "$choices" ]] && ! is_blank "$value"; then
        choice_ok=0
        IFS='|' read -r -a allowed_choices <<<"$choices"
        for allowed in "${allowed_choices[@]}"; do
            if [[ "$value" == "$allowed" ]]; then
                choice_ok=1
                break
            fi
        done
        ((choice_ok == 1)) || die "invalid value for --$name: $value (expected ${choices//|/, })"
    fi
done

((option_value[ascension] >= 0 && option_value[ascension] <= 10)) || die "--ascension must be between 0 and 10"
search_max_dop="${option_value[search-max-degree-of-parallelism-for-test]}"
((search_max_dop == -1 || (search_max_dop >= 1 && search_max_dop <= 8))) || \
    die "--search-max-degree-of-parallelism-for-test must be -1 or between 1 and 8"
for name in expected-initial-deep-search-triggered expected-initial-deep-search-improved-result \
    expected-initial-only-death-routes-found expected-initial-act-ending-boss \
    enable-detailed-diagnostic-logs-for-test; do
    value="${option_value[$name]}"
    ((value == -1 || value == 0 || value == 1)) || die "--$name must be -1, 0, or 1"
done
if ((option_value[keep-game-open] == 1 && option_value[exit-on-complete] == 1)); then
    die "--keep-game-open and --exit-on-complete cannot be used together"
fi
if ((option_value[hold-after-initial-search] == 1 && option_value[keep-game-open] == 0)); then
    die "--hold-after-initial-search requires --keep-game-open"
fi
if ((option_value[stop-after-expected-reuse] == 1 && option_value[expected-reused-turn] <= 0)); then
    die "--stop-after-expected-reuse requires --expected-reused-turn"
fi
if ((option_value[stop-after-expected-player-power] == 1)) && is_blank "${option_value[expected-observed-player-power-id]}"; then
    die "--stop-after-expected-player-power requires --expected-observed-player-power-id"
fi
((option_value[timeout-seconds] > 0)) || die "--timeout-seconds must be a positive integer"

for command_name in jq realpath flock setsid pgrep sha256sum; do
    command -v "$command_name" >/dev/null 2>&1 || runtime_error "$command_name is required"
done

game_root="$(realpath -m -- "${option_value[sts2-game-root]}")"
game_executable="$game_root/SlayTheSpire2"
game_mods_root="$game_root/mods"
combat_solver_dll="$game_mods_root/CombatSolver/CombatSolver.dll"
combat_solver_manifest="$game_mods_root/CombatSolver/CombatSolver.json"
ritsu_workshop_root="$(realpath -m -- "${option_value[ritsu-workshop-root]}")"
ritsu_variant_dll="$ritsu_workshop_root/lib/0.111.0/STS2-RitsuLib.dll"
ritsu_manifest_source="$ritsu_workshop_root/mod_manifest.json"
headless_dependency_dir="$game_mods_root/CombatSolverHeadlessRitsuLib"
headless_dependency_marker="$headless_dependency_dir/.combatsolver-headless-only"
interactive_data_dir="$host_data_home/SlayTheSpire2"
headless_root="${COMBATSOLVER_HEADLESS_ROOT:-${XDG_STATE_HOME:-${HOME}/.local/state}/CombatSolver/headless-runtime}"
headless_root="$(realpath -m -- "$headless_root")"
headless_data_home="$headless_root/data"
headless_config_home="$headless_root/config"
headless_cache_home="$headless_root/cache"
data_dir="$headless_data_home/SlayTheSpire2"
process_marker_path="$headless_root/process.json"
hold_release_path="$headless_root/release-held-search"
headless_log_path="$headless_root/godot-headless.log"
launcher_log_path="$headless_root/launcher.log"
request_path="$data_dir/combat_solver_test_request.json"
result_path="$data_dir/combat_solver_test_result.json"
ready_path="$data_dir/combat_solver_test_ready.json"
lock_path="$headless_root/launcher.lock"

[[ -x "$game_executable" ]] || runtime_error "game executable not found or not executable: $game_executable"
[[ -f "$combat_solver_dll" && -f "$combat_solver_manifest" ]] || runtime_error \
    "built CombatSolver mod not found under: $game_mods_root/CombatSolver"
[[ -f "$ritsu_variant_dll" && -f "$ritsu_manifest_source" ]] || \
    runtime_error "headless RitsuLib source not found under: $ritsu_workshop_root"
[[ "$(realpath -m -- "$data_dir")" != "$(realpath -m -- "$interactive_data_dir")" ]] || \
    runtime_error "isolated and interactive data directories resolve to the same path"

mkdir -p -- "$headless_root" "$headless_data_home" "$headless_config_home" "$headless_cache_home" "$data_dir"
exec {launcher_lock_fd}>"$lock_path"
flock -w 15 "$launcher_lock_fd" || runtime_error "another unattended launcher owns $lock_path"

if ((option_value[hold-after-initial-search] == 1)) && [[ -f "$hold_release_path" ]]; then
    rm -f -- "$hold_release_path"
fi

copy_interactive_profile_once() {
    [[ ! -d "$data_dir/default" ]] || return 0

    if [[ -d "$interactive_data_dir/default" ]]; then
        cp -a -- "$interactive_data_dir/default" "$data_dir/"
    else
        local account_dir="" candidate
        if [[ -d "$interactive_data_dir/steam" ]]; then
            while IFS= read -r -d '' candidate; do
                if [[ -f "$candidate/settings.save" ]]; then
                    account_dir="$candidate"
                    break
                fi
            done < <(find "$interactive_data_dir/steam" -mindepth 1 -maxdepth 1 -type d -print0 | sort -z)
        fi
        [[ -n "$account_dir" ]] || runtime_error \
            "no interactive settings.save was found below $interactive_data_dir"
        mkdir -p -- "$data_dir/default/1"
        cp -a -- "$account_dir/." "$data_dir/default/1/"
    fi

    local directory source target_mods
    for directory in ModConfig mod_configs; do
        source="$interactive_data_dir/$directory"
        [[ ! -d "$source" ]] || cp -a -- "$source" "$data_dir/"
    done
    source="$interactive_data_dir/mods/config"
    if [[ -d "$source" ]]; then
        target_mods="$data_dir/mods"
        mkdir -p -- "$target_mods"
        cp -a -- "$source" "$target_mods/"
    fi
}

copy_interactive_profile_once
settings_path="$data_dir/default/1/settings.save"
[[ -f "$settings_path" ]] || runtime_error \
    "headless settings save not found after profile initialization: $settings_path"
settings_temp="$(mktemp --tmpdir="$data_dir/default/1" .settings.save.XXXXXX)"
if ! jq '.mod_settings = {mods_enabled: true, mod_list: []}' "$settings_path" >"$settings_temp"; then
    rm -f -- "$settings_temp"
    runtime_error "headless settings save is not valid JSON: $settings_path"
fi
chmod --reference="$settings_path" "$settings_temp"
mv -f -- "$settings_temp" "$settings_path"

resolved_progress_snapshot_path=""
if ! is_blank "${option_value[progress-snapshot-path]}"; then
    resolved_progress_snapshot_path="$(realpath -e -- "${option_value[progress-snapshot-path]}")" || \
        runtime_error "progress snapshot not found: ${option_value[progress-snapshot-path]}"
    headless_progress_path="$data_dir/default/1/modded/profile1/saves/progress.save"
    mkdir -p -- "$(dirname -- "$headless_progress_path")"
    cp -f -- "$resolved_progress_snapshot_path" "$headless_progress_path"
fi

resolved_run_snapshot_path=""
if ! is_blank "${option_value[run-snapshot-path]}"; then
    resolved_run_snapshot_path="$(realpath -e -- "${option_value[run-snapshot-path]}")" || \
        runtime_error "run snapshot not found: ${option_value[run-snapshot-path]}"
fi

resolved_replay_state_path=""
if ! is_blank "${option_value[replay-state-path]}"; then
    resolved_replay_state_path="$(realpath -e -- "${option_value[replay-state-path]}")" || \
        runtime_error "replay state not found: ${option_value[replay-state-path]}"
fi

json_array_from_text() {
    local label="$1" source_json="$2" result
    if ! result="$(jq -ce 'if type == "array" then . else [.] end' <<<"$source_json")"; then
        runtime_error "$label is not valid JSON"
    fi
    printf '%s\n' "$result"
}

json_value_from_text() {
    local label="$1" source_json="$2" result
    if ! result="$(jq -ce '.' <<<"$source_json")"; then
        runtime_error "$label is not valid JSON"
    fi
    printf '%s\n' "$result"
}

json_array_from_file() {
    local label="$1" source_path="$2" resolved_path result
    resolved_path="$(realpath -e -- "$source_path")" || runtime_error "$label not found: $source_path"
    if ! result="$(jq -ce 'if type == "array" then . else [.] end' "$resolved_path")"; then
        runtime_error "$label does not contain valid JSON: $resolved_path"
    fi
    printf '%s\n' "$result"
}

json_value_from_file() {
    local label="$1" source_path="$2" resolved_path result
    resolved_path="$(realpath -e -- "$source_path")" || runtime_error "$label not found: $source_path"
    if ! result="$(jq -ce '.' "$resolved_path")"; then
        runtime_error "$label does not contain valid JSON: $resolved_path"
    fi
    printf '%s\n' "$result"
}

array_from_path_or_json() {
    local label="$1" path_option="$2" json_option="$3"
    if ! is_blank "${option_value[$path_option]}"; then
        json_array_from_file "$label" "${option_value[$path_option]}"
    elif ! is_blank "${option_value[$json_option]}"; then
        json_array_from_text "$label" "${option_value[$json_option]}"
    else
        printf '[]\n'
    fi
}

initial_enemy_current_hps='[]'
if ! is_blank "${option_value[initial-enemy-current-hps-json]}"; then
    initial_enemy_current_hps="$(json_array_from_text --initial-enemy-current-hps-json "${option_value[initial-enemy-current-hps-json]}")"
fi
initial_enemy_move_ids='[]'
if ! is_blank "${option_value[initial-enemy-move-ids-json]}"; then
    initial_enemy_move_ids="$(json_array_from_text --initial-enemy-move-ids-json "${option_value[initial-enemy-move-ids-json]}")"
fi
initial_enemy_state_logs='[]'
if ! is_blank "${option_value[initial-enemy-state-logs-json]}"; then
    initial_enemy_state_logs="$(jq -ce '[.]' <<<"${option_value[initial-enemy-state-logs-json]}")" || \
        runtime_error "--initial-enemy-state-logs-json is not valid JSON"
fi
orbs='[]'
if ! is_blank "${option_value[orbs-json]}"; then
    orbs="$(json_array_from_text --orbs-json "${option_value[orbs-json]}")"
fi
powers="$(array_from_path_or_json powers powers-path powers-json)"
relics="$(array_from_path_or_json relics relics-path relics-json)"
combat_relics="$(array_from_path_or_json combat-relics combat-relics-path combat-relics-json)"
cards="$(array_from_path_or_json cards cards-path cards-json)"
run_cards="$(array_from_path_or_json run-cards run-cards-path run-cards-json)"
potions="$(array_from_path_or_json potions potions-path potions-json)"
if [[ "$potions" == '[]' ]] && ! is_blank "${option_value[potion-id]}" && \
    is_blank "${option_value[potions-path]}" && is_blank "${option_value[potions-json]}"; then
    potions="$(jq -cn --arg potionId "${option_value[potion-id]}" '[{potionId: $potionId}]')"
fi

potion_check='null'
potion_checks='[]'
monster_move_check='null'
monster_move_checks='[]'
cards_explicitly_configured=0
if ! is_blank "${option_value[cards-path]}" || ! is_blank "${option_value[cards-json]}"; then
    cards_explicitly_configured=1
fi

if ! is_blank "${option_value[potion-checks-path]}"; then
    potion_checks="$(json_array_from_file potion-checks "${option_value[potion-checks-path]}")"
elif ! is_blank "${option_value[potion-checks-json]}"; then
    potion_checks="$(json_array_from_text potion-checks "${option_value[potion-checks-json]}")"
elif ! is_blank "${option_value[potion-check-path]}"; then
    potion_check="$(json_value_from_file potion-check "${option_value[potion-check-path]}")"
elif ! is_blank "${option_value[potion-check-json]}"; then
    potion_check="$(json_value_from_text potion-check "${option_value[potion-check-json]}")"
elif ! is_blank "${option_value[monster-move-checks-path]}"; then
    monster_move_checks="$(json_array_from_file monster-move-checks "${option_value[monster-move-checks-path]}")"
elif ! is_blank "${option_value[monster-move-checks-json]}"; then
    monster_move_checks="$(json_array_from_text monster-move-checks "${option_value[monster-move-checks-json]}")"
elif is_blank "${option_value[monster-move-id]}"; then
    if [[ "$cards" == '[]' ]] && ((cards_explicitly_configured == 0)) && \
        is_blank "${option_value[run-snapshot-path]}"; then
        cards="$(jq -cn --arg cardId "${option_value[card-id]}" \
            '[{cardId: $cardId, pile: "Hand", count: 1, upgradeLevels: 0}]')"
    fi
else
    expected_player_powers="$(json_value_from_text --expected-player-powers-json "${option_value[expected-player-powers-json]}")"
    expected_enemy_powers="$(json_value_from_text --expected-enemy-powers-json "${option_value[expected-enemy-powers-json]}")"
    monster_move_check="$(jq -cn \
        --arg monsterId "${option_value[monster-id]}" \
        --arg moveId "${option_value[monster-move-id]}" \
        --arg playerLoss "${option_value[expected-player-hp-loss]}" \
        --arg enemyBlock "${option_value[expected-enemy-block-gain]}" \
        --argjson playerPowers "$expected_player_powers" \
        --argjson enemyPowers "$expected_enemy_powers" '
        {
            enemyIndex: 0,
            monsterId: $monsterId,
            moveId: $moveId,
            expectedPlayerHpLoss: (($playerLoss | tonumber) | if . >= 0 then . else null end),
            expectedEnemyBlockGain: (($enemyBlock | tonumber) | if . >= 0 then . else null end),
            expectedPlayerPowers: $playerPowers,
            expectedEnemyPowers: $enemyPowers
        }')"
fi

orb_checks="$(array_from_path_or_json orb-checks orb-checks-path orb-checks-json)"
if ! is_blank "${option_value[power-id]}"; then
    powers="$(jq -cn \
        --arg powerId "${option_value[power-id]}" \
        --arg target "${option_value[power-target]}" \
        --arg amount "${option_value[power-amount]}" \
        '[{powerId: $powerId, target: $target, targetIndex: 0, amount: ($amount | tonumber)}]')"
fi

option_to_field() {
    local option_name="$1" part field="" first=1
    IFS='-' read -r -a parts <<<"$option_name"
    for part in "${parts[@]}"; do
        if ((first == 1)); then
            field="$part"
            first=0
        else
            field+="${part^}"
        fi
    done
    printf '%s\n' "$field"
}

value_arguments=(jq -cn)
wire_arguments=(jq -cn)
for name in "${option_order[@]}"; do
    wire_type="${option_wire[$name]}"
    [[ "$wire_type" != none ]] || continue
    field="$(option_to_field "$name")"
    value_arguments+=(--arg "$field" "${option_value[$name]}")
    wire_arguments+=(--arg "$field" "$wire_type")
done
wire_values="$("${value_arguments[@]}" '$ARGS.named')"
wire_types="$("${wire_arguments[@]}" '$ARGS.named')"

if ((${#modifier_ids[@]} == 0)); then
    modifier_ids_json='[]'
else
    modifier_ids_json="$(printf '%s\0' "${modifier_ids[@]}" | jq -Rs 'split("\u0000")[:-1]')"
fi
if ((${#additional_monster_ids[@]} == 0)); then
    additional_monster_ids_json='[]'
else
    additional_monster_ids_json="$(printf '%s\0' "${additional_monster_ids[@]}" | jq -Rs 'split("\u0000")[:-1]')"
fi
run_id="$(tr -d '-' </proc/sys/kernel/random/uuid)"

request="$(jq -cn \
    --argjson values "$wire_values" \
    --argjson types "$wire_types" \
    --arg runId "$run_id" \
    --arg runSnapshotPath "$resolved_run_snapshot_path" \
    --arg replayStatePath "$resolved_replay_state_path" \
    --argjson initialEnemyCurrentHps "$initial_enemy_current_hps" \
    --argjson initialEnemyMoveIds "$initial_enemy_move_ids" \
    --argjson initialEnemyStateLogs "$initial_enemy_state_logs" \
    --argjson cards "$cards" \
    --argjson runCards "$run_cards" \
    --argjson powers "$powers" \
    --argjson orbs "$orbs" \
    --argjson relics "$relics" \
    --argjson combatRelics "$combat_relics" \
    --argjson potions "$potions" \
    --argjson orbChecks "$orb_checks" \
    --argjson potionCheck "$potion_check" \
    --argjson potionChecks "$potion_checks" \
    --argjson monsterMoveCheck "$monster_move_check" \
    --argjson monsterMoveChecks "$monster_move_checks" \
    --argjson modifierIds "$modifier_ids_json" \
    --argjson additionalMonsterIds "$additional_monster_ids_json" '
    def blank: test("^\\s*$");
    def convert($value; $kind):
        if $kind == "raw_string" then $value
        elif $kind == "raw_int" then ($value | tonumber)
        elif $kind == "bool" then ($value == "1")
        elif $kind == "optional_string" then
            if ($value | blank) then null else $value end
        elif $kind == "positive_int" or $kind == "positive_number" then
            ($value | tonumber) | if . > 0 then . else null end
        elif $kind == "nonnegative_int" or $kind == "nonnegative_number" then
            ($value | tonumber) | if . >= 0 then . else null end
        elif $kind == "tri_bool" then
            ($value | tonumber) | if . >= 0 then (. == 1) else null end
        else error("unknown request wire type: \($kind)")
        end;
    (reduce ($types | to_entries[]) as $entry
        ({}; .[$entry.key] = convert($values[$entry.key]; $entry.value)))
    + {
        schemaVersion: 1,
        runId: $runId,
        runSnapshotPath: (if ($runSnapshotPath | blank) then null else $runSnapshotPath end),
        replayStatePath: (if ($replayStatePath | blank) then null else $replayStatePath end),
        initialEnemyCurrentHps: $initialEnemyCurrentHps,
        initialEnemyMoveIds: $initialEnemyMoveIds,
        initialEnemyStateLogs: $initialEnemyStateLogs,
        cards: $cards,
        runCards: $runCards,
        powers: $powers,
        orbs: $orbs,
        relics: $relics,
        combatRelics: $combatRelics,
        potions: $potions,
        orbChecks: $orbChecks,
        potionCheck: $potionCheck,
        potionChecks: $potionChecks,
        monsterMoveCheck: $monsterMoveCheck,
        monsterMoveChecks: $monsterMoveChecks,
        modifierIds: $modifierIds,
        additionalMonsterIds: $additionalMonsterIds
    }')"

assert_dependency_path() {
    local mods_full dependency_full
    mods_full="$(realpath -m -- "$game_mods_root")"
    dependency_full="$(realpath -m -- "$headless_dependency_dir")"
    [[ "$dependency_full" == "$mods_full/"* ]] || runtime_error \
        "refusing to manage a headless dependency outside the game mods directory: $dependency_full"
}

dependency_created_here=0
install_headless_dependency() {
    assert_dependency_path
    if [[ -d "$headless_dependency_dir" ]]; then
        [[ -f "$headless_dependency_marker" ]] || runtime_error \
            "headless dependency target exists without its ownership marker: $headless_dependency_dir"
        dependency_created_here=1
        rm -rf -- "$headless_dependency_dir"
    else
        dependency_created_here=1
    fi
    mkdir -p -- "$headless_dependency_dir"
    cp -f -- "$ritsu_variant_dll" "$headless_dependency_dir/STS2-RitsuLib.dll"
    cp -f -- "$ritsu_manifest_source" "$headless_dependency_dir/STS2-RitsuLib.json"
    printf '%s\n' 'CombatSolver isolated headless dependency' >"$headless_dependency_marker"
}

remove_headless_dependency() {
    assert_dependency_path
    if [[ ! -d "$headless_dependency_dir" ]]; then
        dependency_created_here=0
        return 0
    fi
    [[ -f "$headless_dependency_marker" ]] || runtime_error \
        "refusing to remove a headless dependency without its ownership marker: $headless_dependency_dir"
    rm -rf -- "$headless_dependency_dir"
    dependency_created_here=0
}

remove_directly_created_headless_dependency() {
    assert_dependency_path
    ((dependency_created_here == 1)) || return 0
    [[ ! -d "$headless_dependency_dir" ]] || rm -rf -- "$headless_dependency_dir"
    dependency_created_here=0
}

process_is_alive() {
    local pid="$1" stat_line process_state
    kill -0 "$pid" 2>/dev/null || return 1
    [[ -r "/proc/$pid/stat" ]] || return 1
    IFS= read -r stat_line <"/proc/$pid/stat" || return 1
    stat_line="${stat_line##*) }"
    process_state="${stat_line%% *}"
    [[ "$process_state" != Z ]]
}

process_is_expected_game() {
    local pid="$1" executable
    [[ -e "/proc/$pid/exe" ]] || return 1
    executable="$(readlink -f -- "/proc/$pid/exe")" || return 1
    [[ "$executable" == "$game_executable" ]]
}

process_start_time_ticks() {
    local pid="$1" stat_line stat_fields
    [[ -r "/proc/$pid/stat" ]] || return 1
    IFS= read -r stat_line <"/proc/$pid/stat" || return 1
    # Everything after the final ") " starts at proc(5) field 3. Field 22 is
    # therefore zero-based element 19, even if the comm field contains spaces.
    stat_line="${stat_line##*) }"
    read -r -a stat_fields <<<"$stat_line"
    ((${#stat_fields[@]} >= 20)) || return 1
    [[ "${stat_fields[19]}" =~ ^[0-9]+$ ]] || return 1
    printf '%s\n' "${stat_fields[19]}"
}

# Return 0 while the exact PID/start-time identity is still alive, 1 once that
# identity has exited (or the PID has been reused), and 2 when /proc cannot be
# inspected conclusively. Cleanup must never treat the indeterminate state as
# proof that the process released the temporary dependency.
process_start_identity_state() {
    local pid="$1" expected_start_time="$2" stat_line process_state current_start_time
    local -a stat_fields=()
    [[ -d "/proc/$pid" ]] || return 1
    [[ -r "/proc/$pid/stat" ]] || return 2
    IFS= read -r stat_line <"/proc/$pid/stat" || return 2
    stat_line="${stat_line##*) }"
    process_state="${stat_line%% *}"
    [[ "$process_state" != Z ]] || return 1
    read -r -a stat_fields <<<"$stat_line"
    ((${#stat_fields[@]} >= 20)) || return 2
    current_start_time="${stat_fields[19]}"
    [[ "$current_start_time" =~ ^[0-9]+$ ]] || return 2
    [[ "$current_start_time" == "$expected_start_time" ]] || return 1
    return 0
}

snapshot_live_game_pids() {
    local output_name="$1" pgrep_output="" pgrep_status=0
    local -n output_ref="$output_name"
    output_ref=()
    if pgrep_output="$(pgrep -x SlayTheSpire2 2>/dev/null)"; then
        mapfile -t output_ref <<<"$pgrep_output"
        return 0
    else
        pgrep_status=$?
    fi
    ((pgrep_status == 1)) || return "$pgrep_status"
}

process_has_expected_headless_environment() {
    local pid="$1" entry found_headless=0 found_data_home=0
    [[ -r "/proc/$pid/environ" ]] || return 1
    while IFS= read -r -d '' entry; do
        case "$entry" in
            COMBATSOLVER_HEADLESS=1) found_headless=1 ;;
            "XDG_DATA_HOME=$headless_data_home") found_data_home=1 ;;
        esac
    done <"/proc/$pid/environ"
    ((found_headless == 1 && found_data_home == 1))
}

process_matches_headless_identity() {
    local pid="$1" expected_start_time="$2" current_start_time
    process_is_alive "$pid" || return 1
    process_is_expected_game "$pid" || return 1
    current_start_time="$(process_start_time_ticks "$pid")" || return 1
    [[ "$current_start_time" == "$expected_start_time" ]] || return 1
    process_has_expected_headless_environment "$pid"
}

remove_process_marker_for_identity() {
    local pid="$1" start_time="$2" marker_pid="" marker_start_time=""
    if [[ -f "$process_marker_path" ]]; then
        marker_pid="$(jq -er '.pid | tostring' "$process_marker_path" 2>/dev/null || true)"
        marker_start_time="$(jq -er '.procStartTimeTicks | tostring' "$process_marker_path" 2>/dev/null || true)"
        if [[ "$marker_pid" == "$pid" && "$marker_start_time" == "$start_time" ]]; then
            rm -f -- "$process_marker_path"
        fi
    fi
}

launched_here=0
owned_cleanup_active=0
stop_test_process_and_remove_dependency() {
    local pid="$1" start_time="$2" stop_deadline identity_status
    if [[ ! "$pid" =~ ^[0-9]+$ || ! "$start_time" =~ ^[0-9]+$ ]]; then
        runtime_error \
            "claimed headless process lacks a usable PID/start-time identity; preserving marker and dependency"
    fi
    if ! process_matches_headless_identity "$pid" "$start_time"; then
        identity_status=0
        process_start_identity_state "$pid" "$start_time" || identity_status=$?
        if ((identity_status != 1)); then
            runtime_error \
                "claimed headless process identity became indeterminate; preserving marker and dependency: pid=$pid"
        fi
    else
        kill -TERM "$pid" 2>/dev/null || true
        stop_deadline=$((SECONDS + 10))
        while ((SECONDS < stop_deadline)); do
            identity_status=0
            process_start_identity_state "$pid" "$start_time" || identity_status=$?
            ((identity_status != 1)) || break
            sleep 0.1
        done
        identity_status=0
        process_start_identity_state "$pid" "$start_time" || identity_status=$?
        if ((identity_status == 0)); then
            kill -KILL "$pid" 2>/dev/null || true
            stop_deadline=$((SECONDS + 10))
            while ((SECONDS < stop_deadline)); do
                identity_status=0
                process_start_identity_state "$pid" "$start_time" || identity_status=$?
                ((identity_status != 1)) || break
                sleep 0.1
            done
            identity_status=0
            process_start_identity_state "$pid" "$start_time" || identity_status=$?
        fi
        if ((identity_status != 1)); then
            runtime_error \
                "claimed headless process did not conclusively exit; preserving marker and dependency: pid=$pid"
        fi
    fi
    if ((launched_here == 1)); then
        wait "$pid" 2>/dev/null || true
    fi
    remove_process_marker_for_identity "$pid" "$start_time"
    remove_headless_dependency
    owned_cleanup_active=0
}

stop_direct_child_and_remove_dependency() {
    local pid="$1" start_time="$2" stop_deadline
    [[ "$pid" =~ ^[0-9]+$ ]] || runtime_error \
        "directly owned headless process lacks a usable PID; preserving the dependency"

    if process_is_alive "$pid"; then
        kill -TERM "$pid" 2>/dev/null || true
        stop_deadline=$((SECONDS + 10))
        while process_is_alive "$pid" && ((SECONDS < stop_deadline)); do
            sleep 0.1
        done
        if process_is_alive "$pid"; then
            kill -KILL "$pid" 2>/dev/null || true
            stop_deadline=$((SECONDS + 10))
            while process_is_alive "$pid" && ((SECONDS < stop_deadline)); do
                sleep 0.1
            done
        fi
    fi
    if process_is_alive "$pid"; then
        runtime_error \
            "directly owned headless process did not exit; preserving marker and dependency: pid=$pid"
    fi
    wait "$pid" 2>/dev/null || true
    if [[ "$start_time" =~ ^[0-9]+$ ]]; then
        remove_process_marker_for_identity "$pid" "$start_time"
    fi
    remove_headless_dependency
    owned_cleanup_active=0
}

cleanup_owned_launcher() {
    local original_status=$?
    trap - EXIT INT TERM HUP
    if ((owned_cleanup_active == 1)); then
        if ((launched_here == 1)) && [[ "$process_pid" =~ ^[0-9]+$ ]]; then
            stop_direct_child_and_remove_dependency "$process_pid" "$process_identity_start_time"
        elif [[ "$process_pid" =~ ^[0-9]+$ ]]; then
            stop_test_process_and_remove_dependency "$process_pid" "$process_identity_start_time"
        else
            remove_directly_created_headless_dependency
            owned_cleanup_active=0
        fi
    fi
    exit "$original_status"
}

arm_owned_cleanup() {
    owned_cleanup_active=1
    trap cleanup_owned_launcher EXIT
    trap 'exit 130' INT
    trap 'exit 143' TERM
    trap 'exit 129' HUP
}

combat_solver_dll_sha256="$(sha256sum -- "$combat_solver_dll")"
combat_solver_dll_sha256="${combat_solver_dll_sha256%% *}"
combat_solver_manifest_sha256="$(sha256sum -- "$combat_solver_manifest")"
combat_solver_manifest_sha256="${combat_solver_manifest_sha256%% *}"

process_pid=""
process_identity_start_time=""
if [[ -f "$process_marker_path" ]]; then
    marker_pid="$(jq -er '.pid | select(type == "number" and . > 0 and floor == .)' "$process_marker_path" 2>/dev/null || true)"
    marker_start_time="$(jq -er '.procStartTimeTicks | strings | select(test("^[0-9]+$"))' "$process_marker_path" 2>/dev/null || true)"
    marker_executable="$(jq -r '.executable // empty' "$process_marker_path" 2>/dev/null || true)"
    marker_data_dir="$(jq -r '.dataDir // empty' "$process_marker_path" 2>/dev/null || true)"
    marker_dll_sha256="$(jq -r '.combatSolverDllSha256 // empty' "$process_marker_path" 2>/dev/null || true)"
    marker_manifest_sha256="$(jq -r '.combatSolverManifestSha256 // empty' "$process_marker_path" 2>/dev/null || true)"
    if [[ -n "$marker_pid" && -n "$marker_start_time" ]] \
        && [[ "$marker_executable" == "$game_executable" ]] \
        && [[ "$marker_data_dir" == "$data_dir" ]] \
        && process_matches_headless_identity "$marker_pid" "$marker_start_time"; then
        process_pid="$marker_pid"
        process_identity_start_time="$marker_start_time"
        arm_owned_cleanup
        if [[ "$marker_dll_sha256" != "$combat_solver_dll_sha256" \
            || "$marker_manifest_sha256" != "$combat_solver_manifest_sha256" ]]; then
            echo "UNATTENDED_RESTART reason=mod_changed pid=$process_pid" >&2
            stop_test_process_and_remove_dependency "$process_pid" "$process_identity_start_time"
            process_pid=""
            process_identity_start_time=""
        elif [[ -f "$ready_path" ]] \
            && [[ "$(jq -r '.held // false' "$ready_path" 2>/dev/null)" == true ]]; then
            # A held protocol host has returned from its request loop and cannot be reused.
            # Restarting also recovers if its launcher was killed before the release marker.
            echo "UNATTENDED_RESTART reason=abandoned_held_process pid=$process_pid" >&2
            stop_test_process_and_remove_dependency "$process_pid" "$process_identity_start_time"
            process_pid=""
            process_identity_start_time=""
        fi
    else
        marker_live_game_pids=()
        snapshot_live_game_pids marker_live_game_pids || runtime_error \
            "could not enumerate game processes while validating the existing marker; marker and dependency were preserved"
        live_marker_processes=()
        for candidate_pid in "${marker_live_game_pids[@]}"; do
            process_is_alive "$candidate_pid" || continue
            live_marker_processes+=("$candidate_pid")
        done
        if ((${#live_marker_processes[@]} > 0)); then
            runtime_error \
                "could not safely discard an invalid headless marker while SlayTheSpire2 is alive; marker and dependency were preserved: pid=$(IFS=,; echo "${live_marker_processes[*]}")"
        fi
        echo "run-unattended-test.sh: discarding stale process marker: $process_marker_path" >&2
        rm -f -- "$process_marker_path"
    fi
fi

game_pids=()
snapshot_live_game_pids game_pids || runtime_error \
    "could not enumerate SlayTheSpire2 processes; marker and dependency were preserved"
other_pids=()
for candidate_pid in "${game_pids[@]}"; do
    process_is_alive "$candidate_pid" || continue
    [[ "$candidate_pid" == "$process_pid" ]] || other_pids+=("$candidate_pid")
done
if ((${#other_pids[@]} > 0)); then
    runtime_error "refusing to run while an unowned SlayTheSpire2 process exists: pid=$(IFS=,; echo "${other_pids[*]}")"
fi

# Publish only after every process-safety check. An already-running protocol
# host may consume the request as soon as the rename becomes visible.
rm -f -- "$ready_path"
request_temp="$(mktemp --tmpdir="$data_dir" ".combat_solver_test_request.$run_id.XXXXXX")"
printf '%s\n' "$request" >"$request_temp"
mv -f -- "$request_temp" "$request_path"

reused_process=0
started_seconds=$SECONDS
if [[ -n "$process_pid" ]]; then
    reused_process=1
else
    launch_cancel_status=0
    trap 'launch_cancel_status=130' INT
    trap 'launch_cancel_status=143' TERM
    trap 'launch_cancel_status=129' HUP
    owned_cleanup_active=1
    trap cleanup_owned_launcher EXIT
    install_headless_dependency
    ((launch_cancel_status == 0)) || exit "$launch_cancel_status"
    launched_here=1
    (
        exec {launcher_lock_fd}>&-
        cd -- "$game_root"
        exec env \
            DISPLAY= \
            WAYLAND_DISPLAY= \
            XDG_DATA_HOME="$headless_data_home" \
            XDG_CONFIG_HOME="$headless_config_home" \
            XDG_CACHE_HOME="$headless_cache_home" \
            COMBATSOLVER_HEADLESS=1 \
            setsid "$game_executable" \
                --headless \
                --disable-vsync \
                --max-fps 0 \
                --force-steam=off \
                --log-file "$headless_log_path"
    ) </dev/null >>"$launcher_log_path" 2>&1 &
    process_pid=$!
    arm_owned_cleanup
    ((launch_cancel_status == 0)) || exit "$launch_cancel_status"
    sleep 0.25
    process_identity_start_time="$(process_start_time_ticks "$process_pid" 2>/dev/null || true)"
    if [[ -z "$process_identity_start_time" ]] \
        || ! process_matches_headless_identity "$process_pid" "$process_identity_start_time"; then
        stop_direct_child_and_remove_dependency "$process_pid" "$process_identity_start_time"
        runtime_error "headless SlayTheSpire2 did not remain running; log=$headless_log_path"
    fi
    process_marker_temp="$(mktemp --tmpdir="$headless_root" .process.json.XXXXXX)"
    jq -cn \
        --argjson pid "$process_pid" \
        --arg startedAtUtc "$(date --utc --iso-8601=seconds)" \
        --arg dataDir "$data_dir" \
        --arg logPath "$headless_log_path" \
        --arg executable "$game_executable" \
        --arg procStartTimeTicks "$process_identity_start_time" \
        --arg combatSolverDllSha256 "$combat_solver_dll_sha256" \
        --arg combatSolverManifestSha256 "$combat_solver_manifest_sha256" \
        '{
            pid: $pid,
            startedAtUtc: $startedAtUtc,
            dataDir: $dataDir,
            logPath: $logPath,
            executable: $executable,
            procStartTimeTicks: $procStartTimeTicks,
            combatSolverDllSha256: $combatSolverDllSha256,
            combatSolverManifestSha256: $combatSolverManifestSha256
        }' \
        >"$process_marker_temp"
    mv -f -- "$process_marker_temp" "$process_marker_path"
fi

if ((reused_process == 1)); then
    echo "UNATTENDED_REUSED run_id=$run_id pid=$process_pid"
else
    echo "UNATTENDED_STARTED run_id=$run_id pid=$process_pid"
fi

result_deadline=$((started_seconds + option_value[timeout-seconds] + 45))
while ((SECONDS < result_deadline)); do
    if [[ -f "$result_path" ]] && result="$(jq -c '.' "$result_path" 2>/dev/null)"; then
        result_run_id="$(jq -r '.runId // empty' <<<"$result")"
        if [[ "$result_run_id" == "$run_id" ]]; then
            jq '.' <<<"$result"
            result_status="$(jq -r '.status // empty' <<<"$result")"
            if [[ "$result_status" != Passed ]]; then
                stop_test_process_and_remove_dependency "$process_pid" "$process_identity_start_time"
                exit 1
            fi
            ready_deadline=$((SECONDS + 120))
            if ((option_value[hold-after-initial-search] == 1)) && [[ "$result_status" == Passed ]]; then
                held_ready=0
                while ((SECONDS < ready_deadline)); do
                    if [[ -f "$ready_path" ]] \
                        && jq -e --arg runId "$run_id" \
                            '.schemaVersion == 1 and .runId == $runId and .held == true' \
                            "$ready_path" >/dev/null 2>&1; then
                        held_ready=1
                        break
                    fi
                    if ! process_matches_headless_identity "$process_pid" "$process_identity_start_time"; then
                        stop_test_process_and_remove_dependency "$process_pid" "$process_identity_start_time"
                        runtime_error "held test process exited before reaching quiescence; run_id=$run_id"
                    fi
                    sleep 0.1
                done
                if ((held_ready == 0)); then
                    stop_test_process_and_remove_dependency "$process_pid" "$process_identity_start_time"
                    runtime_error "held test did not reach quiescence before timeout; run_id=$run_id"
                fi
                echo "UNATTENDED_HELD run_id=$run_id pid=$process_pid release=$hold_release_path"
                while process_matches_headless_identity "$process_pid" "$process_identity_start_time" \
                    && [[ ! -f "$hold_release_path" ]]; do
                    sleep 0.5
                done
                if [[ ! -f "$hold_release_path" ]]; then
                    stop_test_process_and_remove_dependency "$process_pid" "$process_identity_start_time"
                    runtime_error "held test process exited before the release marker was written; run_id=$run_id"
                fi
                rm -f -- "$hold_release_path"
                stop_test_process_and_remove_dependency "$process_pid" "$process_identity_start_time"
                exit 0
            fi
            if ((option_value[exit-on-complete] == 1)); then
                exit_deadline=$((SECONDS + 30))
                while process_matches_headless_identity "$process_pid" "$process_identity_start_time" \
                    && ((SECONDS < exit_deadline)); do
                    sleep 0.1
                done
                stop_test_process_and_remove_dependency "$process_pid" "$process_identity_start_time"
                exit 0
            fi
            while ((SECONDS < ready_deadline)); do
                if [[ -f "$ready_path" ]] \
                    && jq -e --arg runId "$run_id" \
                        '.schemaVersion == 1 and .runId == $runId and .held == false' \
                        "$ready_path" >/dev/null 2>&1; then
                    echo "UNATTENDED_READY run_id=$run_id pid=$process_pid"
                    dependency_created_here=0
                    owned_cleanup_active=0
                    exit 0
                fi
                if ! process_matches_headless_identity "$process_pid" "$process_identity_start_time"; then
                    stop_test_process_and_remove_dependency "$process_pid" "$process_identity_start_time"
                    runtime_error "test passed but its process exited before becoming reusable; run_id=$run_id"
                fi
                sleep 0.1
            done
            stop_test_process_and_remove_dependency "$process_pid" "$process_identity_start_time"
            runtime_error "test passed but did not become reusable before timeout; run_id=$run_id"
        fi
    fi
    if ! process_matches_headless_identity "$process_pid" "$process_identity_start_time"; then
        if ((launched_here == 1)); then
            process_exit_code=0
            wait "$process_pid" || process_exit_code=$?
        else
            process_exit_code="unknown"
        fi
        remove_process_marker_for_identity "$process_pid" "$process_identity_start_time"
        remove_headless_dependency
        runtime_error "game exited without writing a result for this run; exit_code=$process_exit_code"
    fi
    sleep 0.1
done

stop_test_process_and_remove_dependency "$process_pid" "$process_identity_start_time"
runtime_error "unattended test exceeded the launcher timeout; its game process was stopped; run_id=$run_id"
