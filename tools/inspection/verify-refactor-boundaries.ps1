#requires -Version 7.0

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$hotKernelRoot = Join-Path $repositoryRoot "src\HotKernel"
$hotSearchRoot = Join-Path $repositoryRoot "src\HotSearch"
$searchRoot = Join-Path $repositoryRoot "src\Search"
$violations = [System.Collections.Generic.List[string]]::new()

function Strip-CommentLines([string]$path) {
    $lines = [System.IO.File]::ReadAllLines($path)
    $result = [System.Collections.Generic.List[object]]::new($lines.Length)
    for ($i = 0; $i -lt $lines.Length; $i++) {
        $line = $lines[$i]
        if ($line -match '^\s*//') { continue }
        $result.Add([PSCustomObject]@{ LineNumber = $i + 1; Text = $line })
    }
    return $result
}

function Require-Text([string]$relativePath, [string]$text, [string]$category) {
    $fullPath = Join-Path $repositoryRoot $relativePath
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        $violations.Add("${fullPath}: missing required file ($category)")
        return
    }
    if (-not (Select-String -LiteralPath $fullPath -SimpleMatch $text -Quiet)) {
        $violations.Add("${fullPath}: missing $category '$text'")
    }
}

function Forbid-Text([string]$relativePath, [string]$text, [string]$reason) {
    $fullPath = Join-Path $repositoryRoot $relativePath
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) { return }
    foreach ($match in Select-String -LiteralPath $fullPath -SimpleMatch $text) {
        $violations.Add("${fullPath}:$($match.LineNumber): $reason '$text'")
    }
}

# 1. Core directory & entry existence (HotKernel + HotSearch + slim Search DTOs)
foreach ($requiredFile in @(
    'src/HotKernel/State/CombatHotState.cs',
    'src/HotKernel/Import/HotRootBuilder.cs',
    'src/HotKernel/Sim/HotCombatEngine.cs',
    'src/HotSearch/HotSearchCoordinator.cs',
    'src/HotSearch/HotEndTurnRiskEvaluator.cs',
    'src/Search/CombatPlan.cs',
    'src/Search/SearchPolicySnapshot.cs',
    'src/Search/PotionValuationRegistry.cs',
    'src/Engine/Common/PredictionUnsupportedException.cs',
    'src/Prediction/IncompatibleGameplayModException.cs',
    'src/Runtime/InCombatRustKernelBridge.cs',
    'src/Runtime/InCombatRustKernelContracts.cs',
    'src/Runtime/InCombatRustKernelEligibility.cs',
    'src/Runtime/InCombatRustKernelPolicyMapper.cs',
    'src/Runtime/InCombatRustKernelResultMaterializer.cs',
    'src/Runtime/InCombatSolverBackendState.cs',
    'tools/build/RustCombatKernel.targets',
    'rust/Cargo.toml',
    'rust/sts2-ffi/src/host_wire.rs'
)) {
    if (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot $requiredFile) -PathType Leaf)) {
        $violations.Add("Core architecture file missing: $requiredFile")
    }
}

# 2. HotKernel, HotSearch, and slim Search DTO isolation from Runtime/UI/Testing singletons
$forbiddenCoreReferences = @(
    "SolverSettings.Current",
    "Entry.Logger",
    "SolverController",
    "SolverOverlay",
    "SolverText",
    "SolverRelicEffectText",
    "SolverUiModelNames",
    "SolverActionTextIdentity",
    "SolverLocaleRefresh",
    "SolvedRouteCache",
    "RunStatistics",
    "UnattendedTestRunner"
)
$hotKernelFiles = Get-ChildItem -LiteralPath $hotKernelRoot -Filter *.cs -File -Recurse
$hotSearchFiles = Get-ChildItem -LiteralPath $hotSearchRoot -Filter *.cs -File -Recurse
$searchFiles = Get-ChildItem -LiteralPath $searchRoot -Filter *.cs -File -Recurse

foreach ($file in @($hotKernelFiles) + @($hotSearchFiles) + @($searchFiles)) {
    $codeLines = Strip-CommentLines $file.FullName
    foreach ($line in $codeLines) {
        foreach ($reference in $forbiddenCoreReferences) {
            if ($line.Text.Contains($reference, [System.StringComparison]::Ordinal)) {
                $violations.Add("$($file.FullName):$($line.LineNumber): forbidden core reference '$reference'")
            }
        }
    }
}

# 3. HotSearch & HotKernel State/Sim must NOT reference MegaCrit.Sts2.Core.Models
$modelFreeFiles = @($hotSearchFiles) +
    @(Get-ChildItem -LiteralPath (Join-Path $hotKernelRoot "State") -Filter *.cs -File -Recurse) +
    @(Get-ChildItem -LiteralPath (Join-Path $hotKernelRoot "Sim") -Filter *.cs -File -Recurse)
foreach ($file in $modelFreeFiles) {
    foreach ($line in Strip-CommentLines $file.FullName) {
        if ($line.Text.Contains("MegaCrit.Sts2.Core.Models", [System.StringComparison]::Ordinal)) {
            $violations.Add("$($file.FullName):$($line.LineNumber): forbidden game model namespace 'MegaCrit.Sts2.Core.Models'")
        }
    }
}

# 4. No broad catch (Exception) swallowing errors in HotKernel or HotSearch
foreach ($file in @($hotKernelFiles) + @($hotSearchFiles)) {
    foreach ($line in Strip-CommentLines $file.FullName) {
        if ($line.Text -match 'catch\s*\(Exception') {
            $violations.Add("$($file.FullName):$($line.LineNumber): broad catch (Exception) is not allowed in HotKernel/HotSearch")
        }
    }
}

# 5. Zero resurrection of deleted legacy directories, files, or types in src/
foreach ($deletedPath in @(
    'src/Engine/InCombat',
    'src/Search/PowerCardValuation',
    'src/Strategy',
    'src/Testing/Contracts/ThirdParty',
    'src/Search/SimulatedCombatState.cs',
    'src/Search/CombatBeamSolver.cs',
    'src/Search/CombatSearchCoordinator.cs',
    'src/Search/StrategicHpRecoveryBound.cs',
    'src/Search/PrimaryIncumbentTable.cs',
    'src/Search/ISolverBackend.cs',
    'src/Runtime/SearchGcPolicy.cs',
    'src/Runtime/RuntimeGcProfile.cs',
    'src/Engine/Common/MirroredHookListenerFilter.cs',
    'src/Engine/Common/PredictionStateStore.cs',
    'src/Engine/Common/Mirrors/MethodMirrorRegistry.cs',
    'src/HotKernel/Import/HotRootBuilder.SnapshotBridge.cs',
    'src/HotKernel/Import/VanillaRootGate.SnapshotBridge.cs'
)) {
    if (Test-Path -LiteralPath (Join-Path $repositoryRoot $deletedPath)) {
        $violations.Add("Deleted legacy path resurrected: $deletedPath")
    }
}

$deletedLegacyTypePattern = '\b(SimulatedCombatState|CombatPredictionSimulator|CombatBeamSolver|CombatSearchCoordinator|SearchGcPolicy|PowerCardValuation|OrderedMutationRetention|CyclePlanning|StrategicHpRecoveryBound|MirroredHookListenerFilter|LegacySolverBackend|MethodMirrorRegistry|PredictionStateStore|PrimaryIncumbentTable)\b'
foreach ($srcFile in Get-ChildItem -LiteralPath (Join-Path $repositoryRoot "src") -Filter *.cs -File -Recurse) {
    foreach ($line in Strip-CommentLines $srcFile.FullName) {
        $m = [regex]::Match($line.Text, $deletedLegacyTypePattern)
        if ($m.Success) {
            $violations.Add("$($srcFile.FullName):$($line.LineNumber): deleted legacy type resurrected '$($m.Value)'")
        }
    }
}

# 6. Runtime ownership & root snapshot boundaries
Require-Text 'src/Runtime/CombatRootSnapshot.cs' 'main thread' 'root snapshot main-thread boundary'
Require-Text 'src/Runtime/CombatRootSnapshot.cs' 'HotRootBuilder.BuildFromLive(' 'HotKernel root capture boundary'
Require-Text 'src/Runtime/SolverController.cs' 'HotSearchCoordinator.Solve(' 'HotSearch direct solve boundary'
Require-Text 'src/Runtime/SolverController.cs' 'InCombatRustKernelBridge.' 'RustKernel host bridge boundary'
Require-Text 'src/HotSearch/HotPlanMaterializer.cs' 'MaterializeFromSteps(' 'HotPlanMaterializer step replay boundary'
Require-Text 'src/Runtime/SolverController.cs' 'SolverOverlaySnapshot.' 'overlay snapshot projection boundary'
foreach ($sessionType in @('class SolverCombatSession', 'class SolverSearchSession', 'class SolverDeploymentSession')) {
    Require-Text 'src/Runtime/SolverControllerSessions.cs' $sessionType 'controller session boundary'
}
foreach ($retiredField in @(
    '_searchCancellation', '_deploymentCancellation', '_generation', '_searching',
    '_deployAfterSearch', '_searchStamp', '_searchProgress', '_renderedProgress'
)) {
    Forbid-Text 'src/Runtime/SolverController.cs' $retiredField 'retired controller field returned'
}

# 7. UI immutable snapshot boundary (renderers only read SolverOverlaySnapshot)
Require-Text 'src/UI/SolverOverlaySnapshot.cs' 'internal sealed record SolverOverlaySnapshot(' 'overlay snapshot boundary'
Require-Text 'src/UI/SolverOverlaySnapshot.cs' 'public static SolverOverlaySnapshot Capture(SolverResult result, bool unexpectedReplan)' 'overlay snapshot capture boundary'
Require-Text 'src/UI/SolverOverlay.cs' 'public static void ShowResult(Node host, SolverOverlaySnapshot snapshot)' 'overlay renderer boundary'
Require-Text 'src/UI/SolverRouteRow.cs' 'public void Populate(SolverOverlayTurnSnapshot turn)' 'route row boundary'
Require-Text 'src/UI/SolverActionPill.cs' 'public static Control Create(SolverOverlayActionSnapshot action)' 'action pill boundary'
foreach ($renderer in @(
    'src/UI/SolverOverlay.cs',
    'src/UI/SolverRouteRow.cs',
    'src/UI/SolverActionPill.cs',
    'src/UI/SolverActionBar.cs'
)) {
    foreach ($mutableType in @('SolverResult', 'PlanAction', 'PlanCardChoice', 'ModelDb')) {
        Forbid-Text $renderer $mutableType 'mutable search type returned to renderer'
    }
}

# 8. Native choice & bug-report ownership boundaries
Require-Text 'src/Runtime/NativeChoiceRuntime.cs' 'internal static class NativeChoiceRuntime' 'native choice boundary'
Require-Text 'src/Runtime/PlayerTurnSetupPatches.cs' 'TryGetPlannedTurnSetupChoices' 'turn setup choice boundary'
$nativeChoicePath = Join-Path $repositoryRoot 'src\Runtime\NativeChoiceRuntime.cs'
foreach ($runtimeFile in Get-ChildItem -LiteralPath (Join-Path $repositoryRoot "src\Runtime") -Filter *.cs -File) {
    if ($runtimeFile.FullName -eq $nativeChoicePath) { continue }
    foreach ($match in Select-String -LiteralPath $runtimeFile.FullName -SimpleMatch 'CardSelectCmd.PushSelector') {
        $violations.Add("$($runtimeFile.FullName):$($match.LineNumber): production runtime bypasses native choice UI")
    }
}
Require-Text 'src/Runtime/CombatBugReportUploader.cs' 'AllowAutoRedirect = false' 'bug-report uploader boundary'
Require-Text 'src/Runtime/CombatBugReportUploader.cs' 'UseProxy = false' 'bug-report direct transport boundary'
Forbid-Text 'src/Runtime/CombatBugReportUploader.cs' 'using Godot' 'uploader must not own Godot UI state'
foreach ($legacyLogRead in @('AddFileTail(', 'CaptureLogStarts(', '"*.log"')) {
    Forbid-Text 'src/Runtime/CombatBugReportExporter.cs' $legacyLogRead 'global log collection must stay out of report exports'
}

# 9. Replay, Showcase, and PreCombatForecastWorker isolation
Require-Text 'src/Replay/CheckpointArchive.cs' 'public const string DefaultFixtureSelector = "start";' 'checkpoint default selector boundary'
Require-Text 'src/Runtime/Entry.cs' 'public static bool IsPreCombatWorker' 'pre-combat worker flag boundary'
Require-Text 'src/Runtime/SolverDispatcher.cs' 'if (!Entry.IsPreCombatWorker)' 'pre-combat dispatcher isolation'
Require-Text 'src/Api/PreCombatForecastApi.cs' 'public static class PreCombatForecastApi' 'pre-combat API boundary'
Require-Text 'src/Api/PreCombatForecastWorker.cs' 'COMBATSOLVER_PRECOMBAT_WORKER' 'pre-combat worker boundary'
Require-Text 'src/Api/CombatShowcaseApi.cs' 'public static class CombatShowcaseApi' 'showcase API boundary'
Require-Text 'src/Runtime/CombatShowcaseRuntime.cs' 'SolverController.AcceptShowcaseRoute' 'showcase runtime boundary'
foreach ($apiFile in Get-ChildItem -LiteralPath (Join-Path $repositoryRoot "src\Api") -Filter *.cs -File) {
    foreach ($forbiddenCall in @('SolverController.RequestSearch', 'CombatManager.Instance.SetUpCombat', 'RunManager.Instance.EnterRoomDebug')) {
        foreach ($match in Select-String -LiteralPath $apiFile.FullName -SimpleMatch $forbiddenCall) {
            $violations.Add("$($apiFile.FullName):$($match.LineNumber): pre-combat API directly mutates live combat via '$forbiddenCall'")
        }
    }
}

# 10. Headless .local/headless-instances ownership & Testing host layer separation
foreach ($check in @(
    @('tools/testing/run-unattended-test.sh', 'source "$script_dir/headless-runtime.sh"'),
    @('tools/testing/run-unattended-test.sh', 'add_option cleanup-instance-on-exit 0 switch none'),
    @('tools/testing/run-unattended-test.sh', '$repo_root/.local/headless-instances/$headless_instance'),
    @('tools/testing/run-unattended-test.sh', 'hr_remove_instance'),
    @('tools/testing/run-unattended-test.ps1', ". (Join-Path `$PSScriptRoot 'headless-runtime.ps1')"),
    @('tools/testing/run-unattended-test.ps1', '[switch]$CleanupInstanceOnExit'),
    @('tools/testing/run-unattended-test.ps1', 'Remove-HeadlessRuntimeInstance $runtimeContext'),
    @('tools/testing/headless-runtime.sh', 'hr_remove_instance() {'),
    @('tools/testing/headless-runtime.ps1', 'Join-Path $repository ".local\headless-instances\$Instance"'),
    @('tools/testing/headless-runtime.ps1', 'function Remove-HeadlessRuntimeInstance('),
    @('tools/testing/run-headless-matrix.sh', '--stop-instance'),
    @('tools/testing/run-headless-matrix.ps1', '"-StopInstance"')
)) {
    Require-Text $check[0] $check[1] 'headless infrastructure ownership boundary'
}
foreach ($legacyInstanceRoot in @(
    @('tools/testing/headless-runtime.ps1', 'CombatSolver\headless-instances'),
    @('tools/testing/run-unattended-test.sh', 'CombatSolver/headless-instances'),
    @('tools/testing/run-headless-matrix.sh', 'CombatSolver/headless-instances')
)) {
    Forbid-Text $legacyInstanceRoot[0] $legacyInstanceRoot[1] 'user-local headless instance root returned'
}
foreach ($matrix in @('tools/testing/run-headless-matrix.sh', 'tools/testing/run-headless-matrix.ps1')) {
    Forbid-Text $matrix 'MATRIX-CLEANUP' 'matrix cleanup must not dispatch a new game request'
}
foreach ($helper in @('tools/testing/headless-runtime.sh', 'tools/testing/headless-runtime.ps1')) {
    foreach ($forbidden in @('combat_solver_test_request.json', 'SolverSettings')) {
        Forbid-Text $helper $forbidden 'protocol/game settings leaked into headless resource owner'
    }
}
foreach ($hostCheck in @(
    @('src/Testing/Host/UnattendedTestRunner.cs', 'private static readonly ProtocolHost Host = new();'),
    @('src/Testing/Host/UnattendedTestRunner.ProtocolHost.cs', 'private sealed partial class ProtocolHost'),
    @('src/Testing/Host/UnattendedTestRunner.Writer.cs', 'private sealed partial class Writer('),
    @('src/Testing/Host/UnattendedTestRunner.ScenarioBuilder.cs', 'private sealed partial class ScenarioBuilder('),
    @('src/Testing/Host/UnattendedTestRunner.Assertions.cs', 'private sealed class Assertions('),
    @('src/Testing/Host/UnattendedTestRunner.Executor.cs', 'private sealed class Executor(')
)) {
    Require-Text $hostCheck[0] $hostCheck[1] 'unattended host separation boundary'
}
foreach ($displaced in @('StartNewSingleplayerRun(', 'SolverController.SetFullAuto(')) {
    Forbid-Text 'src/Testing/Host/UnattendedTestRunner.cs' $displaced 'unattended implementation returned to runner entry'
}

if ($violations.Count -gt 0) {
    $violations | ForEach-Object { Write-Error -Message $_ -ErrorAction Continue }
    throw "Refactor boundary verification failed with $($violations.Count) violation(s)."
}

Write-Output "REFACTOR_BOUNDARIES_OK hot_kernel_files=$($hotKernelFiles.Count) hot_search_files=$($hotSearchFiles.Count) search_dto_files=$($searchFiles.Count)"
