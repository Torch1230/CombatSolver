#!/usr/bin/env bash
set -Eeuo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repository_root="$(cd -- "$script_dir/../.." && pwd)"
hot_kernel_root="$repository_root/src/HotKernel"
hot_search_root="$repository_root/src/HotSearch"
search_root="$repository_root/src/Search"
violations=()

has_rg=0
if command -v rg >/dev/null 2>&1; then
    has_rg=1
fi

require_text() {
    local rel="$1" text="$2" category="$3"
    local full="$repository_root/$rel"
    if [[ ! -f "$full" ]]; then
        violations+=("$full: missing required file ($category)")
        return
    fi
    if [[ "$has_rg" -eq 1 ]]; then
        if ! rg -Fq -- "$text" "$full"; then
            violations+=("$full: missing $category '$text'")
        fi
    else
        if ! grep -Fq -- "$text" "$full"; then
            violations+=("$full: missing $category '$text'")
        fi
    fi
}

forbid_text() {
    local rel="$1" text="$2" reason="$3"
    local full="$repository_root/$rel"
    [[ -f "$full" ]] || return 0
    if [[ "$has_rg" -eq 1 ]]; then
        while IFS=: read -r line _; do
            [[ -n "$line" ]] && violations+=("$full:$line: $reason '$text'")
        done < <(rg -Fn -- "$text" "$full" || true)
    else
        while IFS=: read -r line _; do
            [[ -n "$line" ]] && violations+=("$full:$line: $reason '$text'")
        done < <(grep -Fn -- "$text" "$full" || true)
    fi
}

scan_code_for_pattern() {
    local file="$1" pattern="$2" message="$3"
    if [[ "$has_rg" -eq 1 ]]; then
        while IFS=: read -r line text; do
            [[ -z "$line" ]] && continue
            [[ "$text" =~ ^[[:space:]]*// ]] && continue
            violations+=("$file:$line: $message")
        done < <(rg -n -- "$pattern" "$file" || true)
    else
        while IFS=: read -r line text; do
            [[ -z "$line" ]] && continue
            [[ "$text" =~ ^[[:space:]]*// ]] && continue
            violations+=("$file:$line: $message")
        done < <(grep -En -- "$pattern" "$file" || true)
    fi
}

# 1. Core directory & entry existence (HotKernel + HotSearch + slim Search DTOs)
for required_file in \
    src/HotKernel/State/CombatHotState.cs \
    src/HotKernel/Import/HotRootBuilder.cs \
    src/HotKernel/Sim/HotCombatEngine.cs \
    src/HotSearch/HotSearchCoordinator.cs \
    src/HotSearch/HotEndTurnRiskEvaluator.cs \
    src/Search/CombatPlan.cs \
    src/Search/SearchPolicySnapshot.cs \
    src/Search/PotionValuationRegistry.cs \
    src/Engine/Common/PredictionUnsupportedException.cs \
    src/Prediction/IncompatibleGameplayModException.cs \
    src/Runtime/InCombatRustKernelBridge.cs \
    src/Runtime/InCombatRustKernelContracts.cs \
    src/Runtime/InCombatRustKernelEligibility.cs \
    src/Runtime/InCombatRustKernelPolicyMapper.cs \
    src/Runtime/InCombatRustKernelResultMaterializer.cs \
    src/Runtime/InCombatSolverBackendState.cs \
    tools/build/RustCombatKernel.targets \
    rust/Cargo.toml \
    rust/sts2-ffi/src/host_wire.rs; do
    [[ -f "$repository_root/$required_file" ]] || violations+=("Core architecture file missing: $required_file")
done

# 2. HotKernel, HotSearch, and slim Search DTO isolation from Runtime/UI/Testing singletons
forbidden_core_pattern='(SolverSettings\.Current|Entry\.Logger|SolverController|SolverOverlay|SolverText|SolverRelicEffectText|SolverUiModelNames|SolverActionTextIdentity|SolverLocaleRefresh|SolvedRouteCache|RunStatistics|UnattendedTestRunner)'
hot_kernel_files=()
while IFS= read -r f; do hot_kernel_files+=("$f"); done < <(find "$hot_kernel_root" -type f -name '*.cs' | sort)
hot_search_files=()
while IFS= read -r f; do hot_search_files+=("$f"); done < <(find "$hot_search_root" -type f -name '*.cs' | sort)
search_files=()
while IFS= read -r f; do search_files+=("$f"); done < <(find "$search_root" -type f -name '*.cs' | sort)

for file in "${hot_kernel_files[@]}" "${hot_search_files[@]}" "${search_files[@]}"; do
    scan_code_for_pattern "$file" "$forbidden_core_pattern" "forbidden core reference"
done

# 3. HotSearch & HotKernel State/Sim must NOT reference MegaCrit.Sts2.Core.Models
model_free_files=("${hot_search_files[@]}")
while IFS= read -r f; do model_free_files+=("$f"); done < <(find "$hot_kernel_root/State" "$hot_kernel_root/Sim" -type f -name '*.cs' | sort)
for file in "${model_free_files[@]}"; do
    scan_code_for_pattern "$file" 'MegaCrit\.Sts2\.Core\.Models' "forbidden game model namespace 'MegaCrit.Sts2.Core.Models'"
done

# 4. No broad catch (Exception) swallowing errors in HotKernel or HotSearch
for file in "${hot_kernel_files[@]}" "${hot_search_files[@]}"; do
    scan_code_for_pattern "$file" 'catch[[:space:]]*\(Exception' "broad catch (Exception) is not allowed in HotKernel/HotSearch"
done

# 5. Zero resurrection of deleted legacy directories, files, or types in src/
for deleted_path in \
    src/Engine/InCombat \
    src/Search/PowerCardValuation \
    src/Strategy \
    src/Testing/Contracts/ThirdParty \
    src/Search/SimulatedCombatState.cs \
    src/Search/CombatBeamSolver.cs \
    src/Search/CombatSearchCoordinator.cs \
    src/Search/StrategicHpRecoveryBound.cs \
    src/Search/PrimaryIncumbentTable.cs \
    src/Search/ISolverBackend.cs \
    src/Runtime/SearchGcPolicy.cs \
    src/Runtime/RuntimeGcProfile.cs \
    src/Engine/Common/MirroredHookListenerFilter.cs \
    src/Engine/Common/PredictionStateStore.cs \
    src/Engine/Common/Mirrors/MethodMirrorRegistry.cs \
    src/HotKernel/Import/HotRootBuilder.SnapshotBridge.cs \
    src/HotKernel/Import/VanillaRootGate.SnapshotBridge.cs; do
    if [[ -e "$repository_root/$deleted_path" ]]; then
        violations+=("Deleted legacy path resurrected: $deleted_path")
    fi
done

deleted_legacy_pattern='\b(SimulatedCombatState|CombatPredictionSimulator|CombatBeamSolver|CombatSearchCoordinator|SearchGcPolicy|PowerCardValuation|OrderedMutationRetention|CyclePlanning|StrategicHpRecoveryBound|MirroredHookListenerFilter|LegacySolverBackend|MethodMirrorRegistry|PredictionStateStore|PrimaryIncumbentTable)\b'
while IFS= read -r src_file; do
    scan_code_for_pattern "$src_file" "$deleted_legacy_pattern" "deleted legacy type resurrected"
done < <(find "$repository_root/src" -type f -name '*.cs' | sort)

# 6. Runtime ownership & root snapshot boundaries
require_text src/Runtime/CombatRootSnapshot.cs 'main thread' 'root snapshot main-thread boundary'
require_text src/Runtime/CombatRootSnapshot.cs 'HotRootBuilder.BuildFromLive(' 'HotKernel root capture boundary'
require_text src/Runtime/SolverController.cs 'HotSearchCoordinator.Solve(' 'HotSearch direct solve boundary'
require_text src/Runtime/SolverController.cs 'InCombatRustKernelBridge.' 'RustKernel host bridge boundary'
require_text src/HotSearch/HotPlanMaterializer.cs 'MaterializeFromSteps(' 'HotPlanMaterializer step replay boundary'
require_text src/Runtime/SolverController.cs 'SolverOverlaySnapshot.' 'overlay snapshot projection boundary'
for session_type in 'class SolverCombatSession' 'class SolverSearchSession' 'class SolverDeploymentSession'; do
    require_text src/Runtime/SolverControllerSessions.cs "$session_type" 'controller session boundary'
done
for retired_field in \
    _searchCancellation _deploymentCancellation _generation _searching \
    _deployAfterSearch _searchStamp _searchProgress _renderedProgress; do
    forbid_text src/Runtime/SolverController.cs "$retired_field" 'retired controller field returned'
done

# 7. UI immutable snapshot boundary (renderers only read SolverOverlaySnapshot)
require_text src/UI/SolverOverlaySnapshot.cs 'internal sealed record SolverOverlaySnapshot(' 'overlay snapshot boundary'
require_text src/UI/SolverOverlaySnapshot.cs 'public static SolverOverlaySnapshot Capture(SolverResult result, bool unexpectedReplan)' 'overlay snapshot capture boundary'
require_text src/UI/SolverOverlay.cs 'public static void ShowResult(Node host, SolverOverlaySnapshot snapshot)' 'overlay renderer boundary'
require_text src/UI/SolverRouteRow.cs 'public void Populate(SolverOverlayTurnSnapshot turn)' 'route row boundary'
require_text src/UI/SolverActionPill.cs 'public static Control Create(SolverOverlayActionSnapshot action)' 'action pill boundary'
for renderer in \
    src/UI/SolverOverlay.cs \
    src/UI/SolverRouteRow.cs \
    src/UI/SolverActionPill.cs \
    src/UI/SolverActionBar.cs; do
    for mutable_type in SolverResult PlanAction PlanCardChoice ModelDb; do
        forbid_text "$renderer" "$mutable_type" 'mutable search type returned to renderer'
    done
done

# 8. Native choice & bug-report ownership boundaries
require_text src/Runtime/NativeChoiceRuntime.cs 'internal static class NativeChoiceRuntime' 'native choice boundary'
require_text src/Runtime/PlayerTurnSetupPatches.cs 'TryGetPlannedTurnSetupChoices' 'turn setup choice boundary'
while IFS= read -r runtime_file; do
    base="$(basename "$runtime_file")"
    [[ "$base" == "NativeChoiceRuntime.cs" ]] && continue
    forbid_text "src/Runtime/$base" 'CardSelectCmd.PushSelector' 'production runtime bypasses native choice UI'
done < <(find "$repository_root/src/Runtime" -maxdepth 1 -type f -name '*.cs' | sort)
require_text src/Runtime/CombatBugReportUploader.cs 'AllowAutoRedirect = false' 'bug-report uploader boundary'
require_text src/Runtime/CombatBugReportUploader.cs 'UseProxy = false' 'bug-report direct transport boundary'
forbid_text src/Runtime/CombatBugReportUploader.cs 'using Godot' 'uploader must not own Godot UI state'
for legacy_log_read in 'AddFileTail(' 'CaptureLogStarts(' '"*.log"'; do
    forbid_text src/Runtime/CombatBugReportExporter.cs "$legacy_log_read" 'global log collection must stay out of report exports'
done

# 9. Replay, Showcase, and PreCombatForecastWorker isolation
require_text src/Replay/CheckpointArchive.cs 'public const string DefaultFixtureSelector = "start";' 'checkpoint default selector boundary'
require_text src/Runtime/Entry.cs 'public static bool IsPreCombatWorker' 'pre-combat worker flag boundary'
require_text src/Runtime/SolverDispatcher.cs 'if (!Entry.IsPreCombatWorker)' 'pre-combat dispatcher isolation'
require_text src/Api/PreCombatForecastApi.cs 'public static class PreCombatForecastApi' 'pre-combat API boundary'
require_text src/Api/PreCombatForecastWorker.cs 'COMBATSOLVER_PRECOMBAT_WORKER' 'pre-combat worker boundary'
require_text src/Api/CombatShowcaseApi.cs 'public static class CombatShowcaseApi' 'showcase API boundary'
require_text src/Runtime/CombatShowcaseRuntime.cs 'SolverController.AcceptShowcaseRoute' 'showcase runtime boundary'
while IFS= read -r api_file; do
    base="$(basename "$api_file")"
    for forbidden_call in 'SolverController.RequestSearch' 'CombatManager.Instance.SetUpCombat' 'RunManager.Instance.EnterRoomDebug'; do
        forbid_text "src/Api/$base" "$forbidden_call" 'pre-combat API directly mutates live combat via'
    done
done < <(find "$repository_root/src/Api" -maxdepth 1 -type f -name '*.cs' | sort)

# 10. Headless .local/headless-instances ownership & Testing host layer separation
require_text tools/testing/run-unattended-test.sh 'source "$script_dir/headless-runtime.sh"' 'headless infrastructure ownership boundary'
require_text tools/testing/run-unattended-test.sh 'add_option cleanup-instance-on-exit 0 switch none' 'headless infrastructure ownership boundary'
require_text tools/testing/run-unattended-test.sh '$repo_root/.local/headless-instances/$headless_instance' 'headless infrastructure ownership boundary'
require_text tools/testing/run-unattended-test.sh 'hr_remove_instance' 'headless infrastructure ownership boundary'
require_text tools/testing/run-unattended-test.ps1 ". (Join-Path \$PSScriptRoot 'headless-runtime.ps1')" 'headless infrastructure ownership boundary'
require_text tools/testing/run-unattended-test.ps1 '[switch]$CleanupInstanceOnExit' 'headless infrastructure ownership boundary'
require_text tools/testing/run-unattended-test.ps1 'Remove-HeadlessRuntimeInstance $runtimeContext' 'headless infrastructure ownership boundary'
require_text tools/testing/headless-runtime.sh 'hr_remove_instance() {' 'headless infrastructure ownership boundary'
require_text tools/testing/headless-runtime.ps1 'Join-Path $repository ".local\headless-instances\$Instance"' 'headless infrastructure ownership boundary'
require_text tools/testing/headless-runtime.ps1 'function Remove-HeadlessRuntimeInstance(' 'headless infrastructure ownership boundary'
require_text tools/testing/run-headless-matrix.sh '--stop-instance' 'headless infrastructure ownership boundary'
require_text tools/testing/run-headless-matrix.ps1 '"-StopInstance"' 'headless infrastructure ownership boundary'

forbid_text tools/testing/headless-runtime.ps1 'CombatSolver\headless-instances' 'user-local headless instance root returned'
forbid_text tools/testing/run-unattended-test.sh 'CombatSolver/headless-instances' 'user-local headless instance root returned'
forbid_text tools/testing/run-headless-matrix.sh 'CombatSolver/headless-instances' 'user-local headless instance root returned'
for matrix in tools/testing/run-headless-matrix.sh tools/testing/run-headless-matrix.ps1; do
    forbid_text "$matrix" 'MATRIX-CLEANUP' 'matrix cleanup must not dispatch a new game request'
done
for helper in tools/testing/headless-runtime.sh tools/testing/headless-runtime.ps1; do
    for forbidden in 'combat_solver_test_request.json' 'SolverSettings'; do
        forbid_text "$helper" "$forbidden" 'protocol/game settings leaked into headless resource owner'
    done
done
require_text src/Testing/Host/UnattendedTestRunner.cs 'private static readonly ProtocolHost Host = new();' 'unattended host separation boundary'
require_text src/Testing/Host/UnattendedTestRunner.ProtocolHost.cs 'private sealed partial class ProtocolHost' 'unattended host separation boundary'
require_text src/Testing/Host/UnattendedTestRunner.Writer.cs 'private sealed partial class Writer(' 'unattended host separation boundary'
require_text src/Testing/Host/UnattendedTestRunner.ScenarioBuilder.cs 'private sealed partial class ScenarioBuilder(' 'unattended host separation boundary'
require_text src/Testing/Host/UnattendedTestRunner.Assertions.cs 'private sealed class Assertions(' 'unattended host separation boundary'
require_text src/Testing/Host/UnattendedTestRunner.Executor.cs 'private sealed class Executor(' 'unattended host separation boundary'
for displaced in 'StartNewSingleplayerRun(' 'SolverController.SetFullAuto('; do
    forbid_text src/Testing/Host/UnattendedTestRunner.cs "$displaced" 'unattended implementation returned to runner entry'
done

if (( ${#violations[@]} > 0 )); then
    printf '%s\n' "${violations[@]}" >&2
    echo "Refactor boundary verification failed with ${#violations[@]} violation(s)." >&2
    exit 1
fi

echo "REFACTOR_BOUNDARIES_OK hot_kernel_files=${#hot_kernel_files[@]} hot_search_files=${#hot_search_files[@]} search_dto_files=${#search_files[@]}"
