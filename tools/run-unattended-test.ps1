#requires -Version 7.0

param(
    [string]$ScenarioId = "SMOKE-001",
    [string]$CharacterId = "IRONCLAD",
    [string]$Seed = "COMBATSOLVER",
    [string]$EncounterId = "FUZZY_WURM_CRAWLER_WEAK",
    [string]$Sts2GameRoot = "D:\Steam\steamapps\common\Slay the Spire 2",
    [string]$RitsuWorkshopRoot = "D:\Steam\steamapps\workshop\content\2868840\3747602295",
    [string]$RunSnapshotPath = "",
    [string]$ReplayStatePath = "",
    [string]$ExpectedInitialReplayStatePath = "",
    [string]$ProgressSnapshotPath = "",
    [ValidateRange(0, 10)]
    [int]$Ascension = 0,
    [int]$ActIndexForTest = 0,
    [switch]$MarkEncounterAsSecondBossForTest,
    [int]$EnemyCurrentHp = 1,
    [string]$InitialEnemyCurrentHpsJson = "",
    [int]$InitialPlayerHp = -1,
    [int]$InitialPlayerMaxHp = -1,
    [int]$InitialPlayerBlock = -1,
    [int]$InitialPlayerEnergy = -1,
    [int]$InitialPlayerStars = -1,
    [int]$InitialRoundNumber = -1,
    [int]$InitialPlayerTurnNumber = -1,
    [string]$InitialEnemyStateLogsJson = "",
    [switch]$ReloadRunRngAfterStateInjection,
    [string]$CardId = "STRIKE_IRONCLAD",
    [string]$PowerId = "",
    [string]$PowersJson = "",
    [string]$PowersPath = "",
    [int]$PowerAmount = 1,
    [ValidateSet("Player", "Enemy")]
    [string]$PowerTarget = "Enemy",
    [string]$MonsterMoveId = "",
    [string]$MonsterId = "",
    [int]$ExpectedPlayerHpLoss = -1,
    [int]$ExpectedEnemyBlockGain = -1,
    [string]$ExpectedPlayerPowersJson = "{}",
    [string]$ExpectedEnemyPowersJson = "{}",
    [string]$MonsterMoveChecksJson = "",
    [string]$MonsterMoveChecksPath = "",
    [string]$OrbsJson = "",
    [string]$OrbChecksJson = "",
    [string]$OrbChecksPath = "",
    [string]$RelicsJson = "",
    [string]$RelicsPath = "",
    [string]$CombatRelicsJson = "",
    [string]$CombatRelicsPath = "",
    [string]$CardsJson = "",
    [string]$CardsPath = "",
    [string]$ReplayStateCardsPath = "",
    [string]$RunCardsJson = "",
    [string]$RunCardsPath = "",
    [string]$PotionCheckJson = "",
    [string]$PotionCheckPath = "",
    [string]$PotionChecksJson = "",
    [string]$PotionChecksPath = "",
    [string]$PotionId = "",
    [string]$PotionsJson = "",
    [string]$PotionsPath = "",
    [string[]]$ModifierId = @(),
    [string[]]$AdditionalMonsterId = @(),
    [string]$InitialEnemyMoveIdsJson = "",
    [int]$ExpectedFinishedTurn = 0,
    [int]$ExpectedFinishedTurnAtMost = 0,
    [int]$ExpectedFinishedPlayerHpAtLeast = -1,
    [switch]$ClearPlayerHand,
    [switch]$ClearPlayerPiles,
    [switch]$ClearRunDeck,
    [switch]$ClearAllPowers,
    [switch]$VerifyPredictionFailureBoundaries,
    [switch]$VerifySearchPolicySnapshot,
    [switch]$VerifyControllerSessionLifecycle,
    [switch]$VerifyForkBoundaries,
    [switch]$VerifyCombatRootSnapshot,
    [switch]$VerifyBaseLibCardModifierBoundary,
    [switch]$StopAfterCombatRootSnapshotAssertion,
    [switch]$VerifyIncrementalSearch,
    [switch]$ForceShortSearchOnly,
    [switch]$MeasureSearchPhases,
    [ValidateSet(-1, 1, 2, 3, 4, 5, 6, 7, 8)]
    [int]$SearchMaxDegreeOfParallelismForTest = -1,
    [switch]$HoldAfterInitialSearch,
    [int]$ShortSearchBudgetOverrideMilliseconds = -1,
    [int]$DeepSearchBudgetOverrideMilliseconds = -1,
    [ValidateSet("", "Short", "Deep")]
    [string]$ExpectedInitialSearchPhase = "",
    [ValidateSet(-1, 0, 1)]
    [int]$ExpectedInitialDeepSearchTriggered = -1,
    [ValidateSet(-1, 0, 1)]
    [int]$ExpectedInitialDeepSearchImprovedResult = -1,
    [double]$ExpectedInitialTotalElapsedMillisecondsAtMost = -1,
    [long]$ExpectedInitialTotalAllocatedBytesAtMost = -1,
    [int]$ExpectedInitialGen2CollectionsAtMost = -1,
    [double]$ExpectedInitialTotalGcPauseMillisecondsAtMost = -1,
    [double]$ExpectedInitialMaxGcPauseMillisecondsAtMost = -1,
    [double]$ExpectedInitialMaxMainThreadFrameGapMillisecondsAtMost = -1,
    [int]$ExpectedInitialMainThreadFramesOver50MillisecondsAtMost = -1,
    [int]$ExpectedInitialMainThreadFramesOver100MillisecondsAtMost = -1,
    [int]$ExpectedInitialTransitionCacheHitsAtLeast = -1,
    [int]$ExpectedInitialRepeatableNoProgressBranchesPrunedAtLeast = -1,
    [int]$ExpectedInitialCycleShapesDetectedAtLeast = -1,
    [int]$ExpectedInitialCycleProbeContinuationsExpandedAtLeast = -1,
    [int]$ExpectedInitialCycleCandidatesProtectedAtLeast = -1,
    [int]$ExpectedInitialCycleContinuationsStoppedAtLeast = -1,
    [int]$ExpectedInitialCrossTurnCandidatesProtectedAtLeast = -1,
    [int]$ExpectedInitialCrossTurnContinuationsStoppedAtLeast = -1,
    [int]$ExpectedInitialNodeLimitSnapshotsReleasedAtLeast = -1,
    [int]$ExpectedInitialChoiceBranchesEvaluatedAtLeast = -1,
    [int]$ExpectedInitialExecutableActionCountAtLeast = -1,
    [int]$ExpectedInitialSoldHp = -1,
    [int]$ExpectedInitialSoldHpAtMost = -1,
    [int]$ExpectedInitialSoldHpBranchesPrunedAtLeast = -1,
    [int]$ExpectedInitialActionAdmissionRepresentativesProtectedAtLeast = -1,
    [int]$ExpectedInitialHpInvestmentBranchesProtectedAtLeast = -1,
    [int]$ExpectedInitialPotionCount = -1,
    [int]$ExpectedInitialPotionHpSavedAtLeast = -1,
    [int]$ExpectedInitialPotionBranchesRejectedAtLeast = -1,
    [ValidateSet("", "PreserveResources", "LetEscape")]
    [string]$ExpectedInitialTheftPolicy = "",
    [int]$ExpectedInitialOutstandingStolenResource = -1,
    [int]$ExpectedInitialSearchedTurnsAtLeast = -1,
    [int]$ExpectedInitialShufflesCrossedAtLeast = -1,
    [int]$ExpectedInitialUnmirroredCount = -1,
    [int]$ExpectedInitialHpLostAtMost = -1,
    [int]$ExpectedInitialProjectedBattleHpLost = -1,
    [int]$ExpectedInitialProjectedBattleHpLostAtMost = -1,
    [int]$ExpectedInitialLongTermResourceValueAtLeast = -1,
    [int]$ExpectedInitialFinalMaxHp = -1,
    [int]$ExpectedInitialMaxBlockAtLeast = -1,
    [int]$ExpectedInitialActualBlockAtLeast = -1,
    [string]$ExpectedInitialActionCardId = "",
    [string]$ExpectedInitialAbsentActionCardId = "",
    [string]$ExpectedInitialFirstActionCardId = "",
    [string]$ExpectedInitialFirstActionPotionId = "",
    [string]$ExpectedInitialActionTitle = "",
    [int]$ExpectedInitialActionReplayCount = -1,
    [ValidateSet(-1, 0, 1)]
    [int]$ExpectedInitialOnlyDeathRoutesFound = -1,
    [int]$ExpectedInitialCombatEndedTurn = 0,
    [int]$ExpectedInitialDeathTurn = 0,
    [int]$ExpectedInitialDeathTurnAtLeast = 0,
    [int]$ExpectedInitialFinalEnemyHpAtMost = -1,
    [ValidateSet(-1, 0, 1)]
    [int]$ExpectedInitialActEndingBoss = -1,
    [ValidateSet("", "None", "ActClearHeal", "RunEnding")]
    [string]$ExpectedInitialBossHpRelief = "",
    [string]$ExpectedInitialPlannedChoiceCardId = "",
    [int]$ExpectedInitialTurnStartChoiceTurn = 0,
    [string]$ExpectedInitialTurnStartChoiceSourceId = "",
    [string]$ExpectedInitialTurnStartChoiceCardId = "",
    [string]$ExpectedInitialTurnStartChoiceStateContains = "",
    [string]$ExpectedInitialTurnStartChoiceStateExcludes = "",
    [int]$ExpectedInitialSetupChoiceCountAtLeast = -1,
    [string]$ExpectedInitialSetupChoiceSourceId = "",
    [string]$ExpectedInitialSetupChoiceTextStartsWith = "",
    [switch]$VerifyInitialSetupWaitsForUserStart,
    [switch]$VerifyTurnSetupManualRecalculate,
    [switch]$VerifyTurnSetupManualRefresh,
    [switch]$VerifyTurnSetupControlsDuringInitialSearch,
    [switch]$VerifyTurnSetupSceneExitCancellation,
    [switch]$StopAfterInitialSetupAssertion,
    [switch]$StopAfterInitialSolverResultAssertion,
    [switch]$ExpectedFullAutoPausedAtDeathTurn,
    [switch]$ExpectedFullAutoPausedAfterWorseRecalculation,
    [switch]$ExpectedFullAutoPausedAtLiveRisk,
    [switch]$EnableStopOnWorseRecalculationForTest,
    [string]$ExpectedInitialRelicEffectId = "",
    [string]$ExpectedInitialRelicEffectSummary = "",
    [int]$ExpectedReusedTurn = 0,
    [int]$ExpectedReusedProjectedBattleHpLost = -1,
    [int]$ExpectedUnexpectedReplansAtMost = -1,
    [switch]$StopAfterExpectedReuse,
    [string]$ExpectedPlayedCardId = "",
    [string]$ExpectedUsedPotionId = "",
    [string]$ExpectedObservedPlayerPowerId = "",
    [string]$ExpectedNativeChoiceOwnerPrefix = "",
    [ValidateSet("", "ChooseCard", "SimpleGrid", "CombatPile", "Hand", "HandUpgrade")]
    [string]$ExpectedNativeChoiceSurface = "",
    [int]$ExpectedNativeChoiceVisibleAtLeast = -1,
    [int]$ExpectedNativeChoiceSearchStartedAtMost = -1,
    [switch]$StopAfterExpectedPlayerPower,
    [switch]$ExpectedPlayerDeath,
    [ValidateSet("", "FollowGame", "Normal", "Fast", "Instant")]
    [string]$HeadlessFastModeForTest = "",
    [ValidateSet("", "FollowGame", "Normal", "Fast", "Instant")]
    [string]$DeploymentFastModeForTest = "",
    [ValidateSet("", "Low", "Medium", "High", "VeryHigh", "Custom")]
    [string]$PerformancePresetForTest = "",
    [int]$ShortMaxCardBranchesPerNodeForTest = -1,
    [int]$DeepMaxCardBranchesPerNodeForTest = -1,
    [ValidateSet("", "Disabled", "Smart", "RequireAtLeastOne")]
    [string]$PotionPolicyForTest = "",
    [string]$PotionDirectivesForTestJson = "",
    [ValidateSet("", "ProgressionFirst", "MinimizeHpLoss")]
    [string]$ActTransitionBossHpStrategyForTest = "",
    [ValidateSet("", "ProgressionFirst", "MinimizeHpLoss")]
    [string]$FinalBossHpStrategyForTest = "",
    [ValidateSet("", "PreserveResources", "LetEscape")]
    [string]$TheftPolicyForTest = "",
    [ValidateSet(-1, 0, 1)]
    [int]$EnableNoGcRegionForTest = -1,
    [double]$NoGcRegionBudgetGigabytesForTest = -1,
    [double]$DeploymentInterActionDelaySecondsForTest = -1,
    [switch]$AssertDeploymentSpeedRestored,
    [switch]$ExportBugReportAfterSetup,
    [switch]$ExportBugReportAfterCombat,
    [ValidateSet(-1, 0, 1)]
    [int]$EnableDetailedDiagnosticLogsForTest = -1,
    [switch]$ManualEndTurnAfterInitialSearch,
    [switch]$SingleStepAfterInitialSearch,
    [ValidateSet("", "ExecuteCurrentTurn", "FullAuto")]
    [string]$SingleStepResumeModeForTest = "",
    [int]$ExpectedTurnSetupToDeploymentDelayMillisecondsAtLeast = -1,
    [switch]$EnableFullAutoAfterManualEndTurn,
    [int]$ExpectedManualDivergencesAtLeast = -1,
    [int]$ExpectedUnexpectedReplansAtLeast = -1,
    [switch]$StopAfterExpectedUnexpectedReplan,
    [switch]$ExpectedUnexpectedReplanWarning,
    [switch]$ExportBugReportAfterUnexpectedReplan,
    [ValidateSet("", "solver_only", "manual_plus_solver")]
    [string]$ExpectedBugReportControlMode = "",
    [int]$ExpectedNoGcRegionRolloversAtLeast = -1,
    [int]$InjectPlayerHpLossBeforeAutoSearchTurn = 0,
    [int]$InjectPlayerHpLossAmount = 0,
    [int]$ClearPlayerBlockBeforeEndTurnForTest = 0,
    [int]$TimeoutSeconds = 150,
    [switch]$KeepGameOpen,
    [switch]$ExitOnComplete
)

$ErrorActionPreference = "Stop"

if ($null -eq ("CombatSolverUnattendedLauncherCancellation" -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Threading;

public static class CombatSolverUnattendedLauncherCancellation
{
    private static int requested;
    private static int installed;

    public static bool IsCancellationRequested => Volatile.Read(ref requested) != 0;

    public static void Install()
    {
        Interlocked.Exchange(ref requested, 0);
        if (Interlocked.Exchange(ref installed, 1) == 0)
            Console.CancelKeyPress += HandleCancelKeyPress;
    }

    public static void Uninstall()
    {
        if (Interlocked.Exchange(ref installed, 0) != 0)
            Console.CancelKeyPress -= HandleCancelKeyPress;
    }

    private static void HandleCancelKeyPress(object sender, ConsoleCancelEventArgs args)
    {
        args.Cancel = true;
        Interlocked.Exchange(ref requested, 1);
    }
}
"@
}

function Assert-LauncherNotCancelled {
    if ([CombatSolverUnattendedLauncherCancellation]::IsCancellationRequested) {
        throw [OperationCanceledException]::new("Unattended launcher cancellation was requested.")
    }
}
$gameRoot = [IO.Path]::GetFullPath($Sts2GameRoot)
$gameExe = Join-Path $gameRoot "SlayTheSpire2.exe"
$gameModsRoot = Join-Path $gameRoot "mods"
$combatSolverDll = Join-Path $gameModsRoot "CombatSolver\CombatSolver.dll"
$combatSolverManifest = Join-Path $gameModsRoot "CombatSolver\CombatSolver.json"
$resolvedRitsuWorkshopRoot = [IO.Path]::GetFullPath($RitsuWorkshopRoot)
$ritsuVariantDll = Join-Path $resolvedRitsuWorkshopRoot "lib\0.111.0\STS2-RitsuLib.dll"
$ritsuManifestSource = Join-Path $resolvedRitsuWorkshopRoot "mod_manifest.json"
$headlessDependencyDir = Join-Path $gameModsRoot ".combatsolver-headless-ritsulib"
$headlessDependencyMarker = Join-Path $headlessDependencyDir ".combatsolver-headless-only"
$interactiveDataDir = Join-Path ([Environment]::GetFolderPath("ApplicationData")) "SlayTheSpire2"
$headlessRoot = Join-Path ([Environment]::GetFolderPath("LocalApplicationData")) "CombatSolver\headless-runtime"
$headlessRoaming = Join-Path $headlessRoot "Roaming"
$headlessLocal = Join-Path $headlessRoot "Local"
$dataDir = Join-Path $headlessRoaming "SlayTheSpire2"
$processMarkerPath = Join-Path $headlessRoot "process.json"
$holdReleasePath = Join-Path $headlessRoot "release-held-search"
$headlessLogPath = Join-Path $headlessRoot "godot-headless.log"
$requestPath = Join-Path $dataDir "combat_solver_test_request.json"
$resultPath = Join-Path $dataDir "combat_solver_test_result.json"
$readyPath = Join-Path $dataDir "combat_solver_test_ready.json"
$launcherLockPath = Join-Path $headlessRoot "launcher.lock"

if (-not (Test-Path -LiteralPath $gameExe -PathType Leaf)) {
    throw "Game executable not found: $gameExe"
}
if (-not (Test-Path -LiteralPath $combatSolverDll -PathType Leaf) -or
    -not (Test-Path -LiteralPath $combatSolverManifest -PathType Leaf)) {
    throw "Built CombatSolver mod not found under: $(Join-Path $gameModsRoot 'CombatSolver')"
}
if (-not (Test-Path -LiteralPath $ritsuVariantDll -PathType Leaf) -or
    -not (Test-Path -LiteralPath $ritsuManifestSource -PathType Leaf)) {
    throw "Headless RitsuLib source not found under: $resolvedRitsuWorkshopRoot"
}
if ([string]::Equals(
        [IO.Path]::GetFullPath($dataDir),
        [IO.Path]::GetFullPath($interactiveDataDir),
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Isolated and interactive data directories resolve to the same path: $dataDir"
}
if ($KeepGameOpen.IsPresent -and $ExitOnComplete.IsPresent) {
    throw "KeepGameOpen and ExitOnComplete cannot be used together."
}
if ($HoldAfterInitialSearch.IsPresent -and -not $KeepGameOpen.IsPresent) {
    throw "HoldAfterInitialSearch requires KeepGameOpen so the profiler can attach to the held combat."
}
if ($StopAfterExpectedReuse.IsPresent -and $ExpectedReusedTurn -le 0) {
    throw "StopAfterExpectedReuse requires ExpectedReusedTurn."
}
if ($StopAfterExpectedPlayerPower.IsPresent -and [string]::IsNullOrWhiteSpace($ExpectedObservedPlayerPowerId)) {
    throw "StopAfterExpectedPlayerPower requires ExpectedObservedPlayerPowerId."
}

New-Item -ItemType Directory -Path $headlessRoot -Force | Out-Null
$launcherLock = $null
$launcherLockDeadline = (Get-Date).AddSeconds(15)
while ($null -eq $launcherLock) {
    try {
        $launcherLock = [IO.File]::Open(
            $launcherLockPath,
            [IO.FileMode]::OpenOrCreate,
            [IO.FileAccess]::ReadWrite,
            [IO.FileShare]::None)
    } catch [IO.IOException] {
        if ((Get-Date) -ge $launcherLockDeadline) {
            throw "Another unattended launcher owns $launcherLockPath."
        }
        Start-Sleep -Milliseconds 100
    }
}

$process = $null
$processSafeHandle = $null
$processIdentityStartTimeUtc = ""
$cleanupProcessOnExit = $false
$startedHere = $false
$launcherCancellationInstalled = $false
$launcherFailure = $null
$launcherWasCancelled = $false
try {
[CombatSolverUnattendedLauncherCancellation]::Install()
$launcherCancellationInstalled = $true
Assert-LauncherNotCancelled
if ($HoldAfterInitialSearch.IsPresent -and (Test-Path -LiteralPath $holdReleasePath -PathType Leaf)) {
    Remove-Item -LiteralPath $holdReleasePath -Force
}

function Assert-HeadlessDependencyPath {
    $modsRootFull = [IO.Path]::GetFullPath($gameModsRoot).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $dependencyFull = [IO.Path]::GetFullPath($headlessDependencyDir)
    if (-not $dependencyFull.StartsWith($modsRootFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to manage a headless dependency outside the game mods directory: $dependencyFull"
    }
}

function Install-HeadlessDependency {
    Assert-HeadlessDependencyPath
    if (Test-Path -LiteralPath $headlessDependencyDir -PathType Container) {
        if (-not (Test-Path -LiteralPath $headlessDependencyMarker -PathType Leaf)) {
            throw "Headless dependency target already exists without the ownership marker: $headlessDependencyDir"
        }
        Remove-HeadlessDependency
    }
    New-Item -ItemType Directory -Path $headlessDependencyDir -Force | Out-Null
    # Publish ownership before either loadable file. If a supervising matrix
    # must hard-stop this launcher, it can still identify and remove a partial
    # projection without guessing whether the directory belongs to the user.
    Set-Content -LiteralPath $headlessDependencyMarker -Value "CombatSolver isolated headless dependency" -Encoding UTF8
    Copy-Item -LiteralPath $ritsuVariantDll -Destination (Join-Path $headlessDependencyDir "STS2-RitsuLib.dll")
    Copy-Item -LiteralPath $ritsuManifestSource -Destination (Join-Path $headlessDependencyDir "STS2-RitsuLib.json")
}

function Remove-HeadlessDependency {
    Assert-HeadlessDependencyPath
    if (-not (Test-Path -LiteralPath $headlessDependencyDir -PathType Container)) {
        return
    }
    if (-not (Test-Path -LiteralPath $headlessDependencyMarker -PathType Leaf)) {
        throw "Refusing to remove a headless dependency without the ownership marker: $headlessDependencyDir"
    }
    $knownPayloads = @(
        (Join-Path $headlessDependencyDir "STS2-RitsuLib.dll"),
        (Join-Path $headlessDependencyDir "STS2-RitsuLib.json")
    )
    $knownPaths = @($headlessDependencyMarker) + $knownPayloads
    $unexpected = @(Get-ChildItem -LiteralPath $headlessDependencyDir -Force |
        Where-Object { $_.FullName -notin $knownPaths })
    if ($unexpected.Count -gt 0) {
        throw "Headless dependency target contains unexpected files: $($unexpected.Name -join ',')"
    }
    foreach ($payload in $knownPayloads) {
        if (-not (Test-Path -LiteralPath $payload -PathType Leaf)) {
            continue
        }
        $deadline = (Get-Date).AddSeconds(5)
        while ($true) {
            try {
                Remove-Item -LiteralPath $payload -Force
                break
            } catch {
                if (($_.Exception -isnot [System.IO.IOException] -and
                        $_.Exception -isnot [System.UnauthorizedAccessException]) -or
                    (Get-Date) -ge $deadline) {
                    throw
                }
                Start-Sleep -Milliseconds 100
            }
        }
    }
    Remove-Item -LiteralPath $headlessDependencyMarker -Force
    Remove-Item -LiteralPath $headlessDependencyDir -Force
}

function Get-ProcessStartTimeUtc([Diagnostics.Process]$TestProcess) {
    $TestProcess.Refresh()
    return $TestProcess.StartTime.ToUniversalTime().ToString(
        "O",
        [Globalization.CultureInfo]::InvariantCulture)
}

function ConvertTo-NormalizedUtcTimestamp([object]$Value) {
    if ($null -eq $Value) {
        return $null
    }
    if ($Value -is [DateTimeOffset]) {
        return $Value.UtcDateTime.ToString("O", [Globalization.CultureInfo]::InvariantCulture)
    }
    if ($Value -is [DateTime]) {
        return $Value.ToUniversalTime().ToString("O", [Globalization.CultureInfo]::InvariantCulture)
    }
    $parsed = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParseExact(
            [string]$Value,
            "O",
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::RoundtripKind,
            [ref]$parsed)) {
        return $null
    }
    return $parsed.UtcDateTime.ToString("O", [Globalization.CultureInfo]::InvariantCulture)
}

function Test-ProcessMatchesHeadlessIdentity(
    [Diagnostics.Process]$TestProcess,
    [string]$ExpectedStartTimeUtc,
    [string]$ExpectedExecutable
) {
    if ($null -eq $TestProcess -or
        [string]::IsNullOrWhiteSpace($ExpectedStartTimeUtc) -or
        [string]::IsNullOrWhiteSpace($ExpectedExecutable)) {
        return $false
    }

    # Callers acquire and retain SafeHandle before using this helper. Do not
    # collapse access failures into "not a match": indeterminate ownership must
    # preserve the process, marker, and loaded dependency.
    $TestProcess.Refresh()
    if ($TestProcess.HasExited -or $TestProcess.ProcessName -ne "SlayTheSpire2") {
        return $false
    }
    $actualExecutable = Get-ProcessExecutablePath $TestProcess
    if (-not [string]::Equals(
            $actualExecutable,
            $ExpectedExecutable,
            [StringComparison]::OrdinalIgnoreCase)) {
        return $false
    }
    return [string]::Equals(
        (Get-ProcessStartTimeUtc $TestProcess),
        $ExpectedStartTimeUtc,
        [StringComparison]::Ordinal)
}

function Remove-ProcessMarkerForIdentity(
    [int]$ProcessId,
    [string]$ExpectedStartTimeUtc
) {
    if ($ProcessId -le 0 -or
        [string]::IsNullOrWhiteSpace($ExpectedStartTimeUtc) -or
        -not (Test-Path -LiteralPath $processMarkerPath -PathType Leaf)) {
        return
    }
    try {
        $marker = Get-Content -LiteralPath $processMarkerPath -Raw | ConvertFrom-Json
        $markerStartTimeUtc = ConvertTo-NormalizedUtcTimestamp $marker.processStartTimeUtc
    } catch {
        Write-Warning "Could not validate the process marker during cleanup; leaving it untouched: $processMarkerPath"
        return
    }
    $markerProcessId = 0
    if ([int]::TryParse([string]$marker.pid, [ref]$markerProcessId) -and
        $markerProcessId -eq $ProcessId -and
        [string]::Equals($markerStartTimeUtc, $ExpectedStartTimeUtc, [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $processMarkerPath -Force
    }
}

function Stop-ClaimedProcessAndRemoveDependency(
    [Diagnostics.Process]$TestProcess,
    [string]$ExpectedStartTimeUtc
) {
    $processIdForCleanup = if ($null -eq $TestProcess) { 0 } else { $TestProcess.Id }
    if ($null -eq $TestProcess) {
        throw "Claimed headless process handle is unavailable."
    }

    # Marker recovery and Start-Process both retain this exact Process object's
    # SafeHandle before ownership is claimed. Kill therefore cannot target a
    # later process that happens to reuse the numeric PID.
    $TestProcess.Refresh()
    if (-not $TestProcess.HasExited) {
        $TestProcess.Kill()
        $TestProcess.WaitForExit(10000) | Out-Null
        $TestProcess.Refresh()
    }
    if (-not $TestProcess.HasExited) {
        throw "Claimed headless process did not exit within 10 seconds: pid=$processIdForCleanup"
    }
    # Never remove the temporary dependency while the exact process that loaded
    # it may still be alive. Marker removal remains PID+start-time guarded.
    Remove-ProcessMarkerForIdentity $processIdForCleanup $ExpectedStartTimeUtc
    Remove-HeadlessDependency
}

New-Item -ItemType Directory -Path $dataDir -Force | Out-Null
New-Item -ItemType Directory -Path $headlessLocal -Force | Out-Null
if (-not (Test-Path -LiteralPath (Join-Path $dataDir "default") -PathType Container)) {
    foreach ($directory in @("default", "ModConfig", "mod_configs")) {
        $source = Join-Path $interactiveDataDir $directory
        if (Test-Path -LiteralPath $source -PathType Container) {
            Copy-Item -LiteralPath $source -Destination $dataDir -Recurse -Force
        }
    }
    $sourceModConfig = Join-Path $interactiveDataDir "mods\config"
    if (Test-Path -LiteralPath $sourceModConfig -PathType Container) {
        $targetMods = Join-Path $dataDir "mods"
        New-Item -ItemType Directory -Path $targetMods -Force | Out-Null
        Copy-Item -LiteralPath $sourceModConfig -Destination $targetMods -Recurse -Force
    }
}
$settingsPath = Join-Path $dataDir "default\1\settings.save"
if (-not (Test-Path -LiteralPath $settingsPath -PathType Leaf)) {
    throw "Headless settings save not found after profile initialization: $settingsPath"
}
$headlessSettings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
$headlessSettings.mod_settings = [ordered]@{
    mods_enabled = $true
    mod_list = @()
}
$headlessSettings | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $settingsPath -Encoding UTF8
$resolvedProgressSnapshotPath = if ([string]::IsNullOrWhiteSpace($ProgressSnapshotPath)) {
    $null
} else {
    (Resolve-Path -LiteralPath $ProgressSnapshotPath).Path
}
if ($null -ne $resolvedProgressSnapshotPath) {
    $headlessProgressPath = Join-Path $dataDir "default\1\modded\profile1\saves\progress.save"
    $headlessProgressDirectory = Split-Path -Parent $headlessProgressPath
    New-Item -ItemType Directory -Path $headlessProgressDirectory -Force | Out-Null
    Copy-Item -LiteralPath $resolvedProgressSnapshotPath -Destination $headlessProgressPath -Force
}
$resolvedRunSnapshotPath = if ([string]::IsNullOrWhiteSpace($RunSnapshotPath)) {
    $null
} else {
    (Resolve-Path -LiteralPath $RunSnapshotPath).Path
}

function Get-ProcessExecutablePath([Diagnostics.Process]$TestProcess) {
    $executable = $TestProcess.MainModule.FileName
    if ([string]::IsNullOrWhiteSpace($executable)) {
        throw "Process $($TestProcess.Id) did not expose its executable path."
    }
    return [IO.Path]::GetFullPath($executable)
}
$resolvedReplayStatePath = if ([string]::IsNullOrWhiteSpace($ReplayStatePath)) {
    $null
} else {
    (Resolve-Path -LiteralPath $ReplayStatePath).Path
}
$runId = [Guid]::NewGuid().ToString("N")
$replayStateCards = $null
if (-not [string]::IsNullOrWhiteSpace($ReplayStateCardsPath)) {
    $replayState = Get-Content -LiteralPath $ReplayStateCardsPath -Raw | ConvertFrom-Json -Depth 100
    $replayPlayer = @($replayState.players)[0]
    if ($null -eq $replayPlayer) {
        throw "Replay state does not contain a player: $ReplayStateCardsPath"
    }
    $replayStateCards = @(
        foreach ($pile in @($replayPlayer.piles)) {
            foreach ($card in @($pile.cards)) {
                $injection = [ordered]@{
                    cardId = [string]$card.id
                    pile = [string]$pile.pile
                    count = 1
                }
                if ([int]$card.currentUpgradeLevel -gt 0) {
                    $injection.upgradeLevels = [int]$card.currentUpgradeLevel
                }
                if ($null -ne $card.enchantment) {
                    $injection.enchantmentId = [string]$card.enchantment.id
                    $injection.enchantmentAmount = [int]$card.enchantment.amount
                }
                if ($null -ne $card.affliction) {
                    $injection.afflictionId = [string]$card.affliction.id
                    $injection.afflictionAmount = [int]$card.affliction.amount
                }
                $enumMembers = [ordered]@{}
                foreach ($field in @($card.fields.PSObject.Properties)) {
                    if ($field.Name -match '\.(?<_member>_tinkerTimeType|_tinkerTimeRider)$') {
                        $enumMembers[$Matches['_member']] = [string]$field.Value
                    }
                }
                if ($enumMembers.Count -gt 0) {
                    $injection.enumMembers = [pscustomobject]$enumMembers
                }
                [pscustomobject]$injection
            }
        }
    )
}
$cardsExplicitlyConfigured = -not [string]::IsNullOrWhiteSpace($CardsPath) -or
    -not [string]::IsNullOrWhiteSpace($CardsJson) -or
    $null -ne $replayStateCards
$request = [ordered]@{
    schemaVersion = 1
    runId = $runId
    scenarioId = $ScenarioId
    characterId = $CharacterId
    encounterId = $EncounterId
    runSnapshotPath = $resolvedRunSnapshotPath
    replayStatePath = $resolvedReplayStatePath
    expectedInitialReplayStatePath = if ([string]::IsNullOrWhiteSpace($ExpectedInitialReplayStatePath)) { $null } else { (Resolve-Path -LiteralPath $ExpectedInitialReplayStatePath).Path }
    ascension = $Ascension
    actIndexForTest = $ActIndexForTest
    markEncounterAsSecondBossForTest = $MarkEncounterAsSecondBossForTest.IsPresent
    seed = $Seed
    enemyCurrentHp = $EnemyCurrentHp
    initialPlayerHp = if ($InitialPlayerHp -gt 0) { $InitialPlayerHp } else { $null }
    initialPlayerMaxHp = if ($InitialPlayerMaxHp -gt 0) { $InitialPlayerMaxHp } else { $null }
    initialPlayerBlock = if ($InitialPlayerBlock -ge 0) { $InitialPlayerBlock } else { $null }
    initialPlayerEnergy = if ($InitialPlayerEnergy -ge 0) { $InitialPlayerEnergy } else { $null }
    initialPlayerStars = if ($InitialPlayerStars -ge 0) { $InitialPlayerStars } else { $null }
    initialRoundNumber = if ($InitialRoundNumber -ge 0) { $InitialRoundNumber } else { $null }
    initialPlayerTurnNumber = if ($InitialPlayerTurnNumber -ge 0) { $InitialPlayerTurnNumber } else { $null }
    initialEnemyStateLogs = if ([string]::IsNullOrWhiteSpace($InitialEnemyStateLogsJson)) { @() } else { @($InitialEnemyStateLogsJson | ConvertFrom-Json -NoEnumerate) }
    reloadRunRngAfterStateInjection = $ReloadRunRngAfterStateInjection.IsPresent
    cards = @()
    runCards = @()
    powers = @()
    orbs = @()
    relics = @()
    combatRelics = @()
    potions = @()
    orbChecks = @()
    potionCheck = $null
    potionChecks = @()
    monsterMoveCheck = $null
    monsterMoveChecks = @()
    modifierIds = @($ModifierId)
    additionalMonsterIds = @($AdditionalMonsterId)
    initialEnemyMoveIds = @()
    timeoutSeconds = $TimeoutSeconds
    expectedFinishedTurn = if ($ExpectedFinishedTurn -gt 0) { $ExpectedFinishedTurn } else { $null }
    expectedFinishedTurnAtMost = if ($ExpectedFinishedTurnAtMost -gt 0) { $ExpectedFinishedTurnAtMost } else { $null }
    expectedFinishedPlayerHpAtLeast = if ($ExpectedFinishedPlayerHpAtLeast -ge 0) { $ExpectedFinishedPlayerHpAtLeast } else { $null }
    clearPlayerHand = $ClearPlayerHand.IsPresent
    clearPlayerPiles = $ClearPlayerPiles.IsPresent -or $null -ne $replayStateCards
    clearRunDeck = $ClearRunDeck.IsPresent
    clearAllPowers = $ClearAllPowers.IsPresent
    verifyPredictionFailureBoundaries = $VerifyPredictionFailureBoundaries.IsPresent
    verifySearchPolicySnapshot = $VerifySearchPolicySnapshot.IsPresent
    verifyControllerSessionLifecycle = $VerifyControllerSessionLifecycle.IsPresent
    verifyForkBoundaries = $VerifyForkBoundaries.IsPresent
    verifyCombatRootSnapshot = $VerifyCombatRootSnapshot.IsPresent
    verifyBaseLibCardModifierBoundary = $VerifyBaseLibCardModifierBoundary.IsPresent
    stopAfterCombatRootSnapshotAssertion = $StopAfterCombatRootSnapshotAssertion.IsPresent
    verifyIncrementalSearch = $VerifyIncrementalSearch.IsPresent
    forceShortSearchOnly = $ForceShortSearchOnly.IsPresent
    measureSearchPhases = $MeasureSearchPhases.IsPresent
    searchMaxDegreeOfParallelismForTest = if ($SearchMaxDegreeOfParallelismForTest -gt 0) { $SearchMaxDegreeOfParallelismForTest } else { $null }
    holdAfterInitialSearch = $HoldAfterInitialSearch.IsPresent
    shortSearchBudgetOverrideMilliseconds = if ($ShortSearchBudgetOverrideMilliseconds -gt 0) { $ShortSearchBudgetOverrideMilliseconds } else { $null }
    deepSearchBudgetOverrideMilliseconds = if ($DeepSearchBudgetOverrideMilliseconds -gt 0) { $DeepSearchBudgetOverrideMilliseconds } else { $null }
    expectedInitialSearchPhase = if ([string]::IsNullOrWhiteSpace($ExpectedInitialSearchPhase)) { $null } else { $ExpectedInitialSearchPhase }
    expectedInitialDeepSearchTriggered = if ($ExpectedInitialDeepSearchTriggered -ge 0) { [bool]$ExpectedInitialDeepSearchTriggered } else { $null }
    expectedInitialDeepSearchImprovedResult = if ($ExpectedInitialDeepSearchImprovedResult -ge 0) { [bool]$ExpectedInitialDeepSearchImprovedResult } else { $null }
    expectedInitialTotalElapsedMillisecondsAtMost = if ($ExpectedInitialTotalElapsedMillisecondsAtMost -ge 0) { $ExpectedInitialTotalElapsedMillisecondsAtMost } else { $null }
    expectedInitialTotalAllocatedBytesAtMost = if ($ExpectedInitialTotalAllocatedBytesAtMost -ge 0) { $ExpectedInitialTotalAllocatedBytesAtMost } else { $null }
    expectedInitialGen2CollectionsAtMost = if ($ExpectedInitialGen2CollectionsAtMost -ge 0) { $ExpectedInitialGen2CollectionsAtMost } else { $null }
    expectedInitialTotalGcPauseMillisecondsAtMost = if ($ExpectedInitialTotalGcPauseMillisecondsAtMost -ge 0) { $ExpectedInitialTotalGcPauseMillisecondsAtMost } else { $null }
    expectedInitialMaxGcPauseMillisecondsAtMost = if ($ExpectedInitialMaxGcPauseMillisecondsAtMost -ge 0) { $ExpectedInitialMaxGcPauseMillisecondsAtMost } else { $null }
    expectedInitialMaxMainThreadFrameGapMillisecondsAtMost = if ($ExpectedInitialMaxMainThreadFrameGapMillisecondsAtMost -ge 0) { $ExpectedInitialMaxMainThreadFrameGapMillisecondsAtMost } else { $null }
    expectedInitialMainThreadFramesOver50MillisecondsAtMost = if ($ExpectedInitialMainThreadFramesOver50MillisecondsAtMost -ge 0) { $ExpectedInitialMainThreadFramesOver50MillisecondsAtMost } else { $null }
    expectedInitialMainThreadFramesOver100MillisecondsAtMost = if ($ExpectedInitialMainThreadFramesOver100MillisecondsAtMost -ge 0) { $ExpectedInitialMainThreadFramesOver100MillisecondsAtMost } else { $null }
    expectedInitialTransitionCacheHitsAtLeast = if ($ExpectedInitialTransitionCacheHitsAtLeast -ge 0) { $ExpectedInitialTransitionCacheHitsAtLeast } else { $null }
    expectedInitialRepeatableNoProgressBranchesPrunedAtLeast = if ($ExpectedInitialRepeatableNoProgressBranchesPrunedAtLeast -ge 0) { $ExpectedInitialRepeatableNoProgressBranchesPrunedAtLeast } else { $null }
    expectedInitialCycleShapesDetectedAtLeast = if ($ExpectedInitialCycleShapesDetectedAtLeast -ge 0) { $ExpectedInitialCycleShapesDetectedAtLeast } else { $null }
    expectedInitialCycleProbeContinuationsExpandedAtLeast = if ($ExpectedInitialCycleProbeContinuationsExpandedAtLeast -ge 0) { $ExpectedInitialCycleProbeContinuationsExpandedAtLeast } else { $null }
    expectedInitialCycleCandidatesProtectedAtLeast = if ($ExpectedInitialCycleCandidatesProtectedAtLeast -ge 0) { $ExpectedInitialCycleCandidatesProtectedAtLeast } else { $null }
    expectedInitialCycleContinuationsStoppedAtLeast = if ($ExpectedInitialCycleContinuationsStoppedAtLeast -ge 0) { $ExpectedInitialCycleContinuationsStoppedAtLeast } else { $null }
    expectedInitialCrossTurnCandidatesProtectedAtLeast = if ($ExpectedInitialCrossTurnCandidatesProtectedAtLeast -ge 0) { $ExpectedInitialCrossTurnCandidatesProtectedAtLeast } else { $null }
    expectedInitialCrossTurnContinuationsStoppedAtLeast = if ($ExpectedInitialCrossTurnContinuationsStoppedAtLeast -ge 0) { $ExpectedInitialCrossTurnContinuationsStoppedAtLeast } else { $null }
    expectedInitialNodeLimitSnapshotsReleasedAtLeast = if ($ExpectedInitialNodeLimitSnapshotsReleasedAtLeast -ge 0) { $ExpectedInitialNodeLimitSnapshotsReleasedAtLeast } else { $null }
    expectedInitialChoiceBranchesEvaluatedAtLeast = if ($ExpectedInitialChoiceBranchesEvaluatedAtLeast -ge 0) { $ExpectedInitialChoiceBranchesEvaluatedAtLeast } else { $null }
    expectedInitialExecutableActionCountAtLeast = if ($ExpectedInitialExecutableActionCountAtLeast -ge 0) { $ExpectedInitialExecutableActionCountAtLeast } else { $null }
    expectedInitialSoldHp = if ($ExpectedInitialSoldHp -ge 0) { $ExpectedInitialSoldHp } else { $null }
    expectedInitialSoldHpAtMost = if ($ExpectedInitialSoldHpAtMost -ge 0) { $ExpectedInitialSoldHpAtMost } else { $null }
    expectedInitialSoldHpBranchesPrunedAtLeast = if ($ExpectedInitialSoldHpBranchesPrunedAtLeast -ge 0) { $ExpectedInitialSoldHpBranchesPrunedAtLeast } else { $null }
    expectedInitialActionAdmissionRepresentativesProtectedAtLeast = if ($ExpectedInitialActionAdmissionRepresentativesProtectedAtLeast -ge 0) { $ExpectedInitialActionAdmissionRepresentativesProtectedAtLeast } else { $null }
    expectedInitialHpInvestmentBranchesProtectedAtLeast = if ($ExpectedInitialHpInvestmentBranchesProtectedAtLeast -ge 0) { $ExpectedInitialHpInvestmentBranchesProtectedAtLeast } else { $null }
    expectedInitialPotionCount = if ($ExpectedInitialPotionCount -ge 0) { $ExpectedInitialPotionCount } else { $null }
    expectedInitialPotionHpSavedAtLeast = if ($ExpectedInitialPotionHpSavedAtLeast -ge 0) { $ExpectedInitialPotionHpSavedAtLeast } else { $null }
    expectedInitialPotionBranchesRejectedAtLeast = if ($ExpectedInitialPotionBranchesRejectedAtLeast -ge 0) { $ExpectedInitialPotionBranchesRejectedAtLeast } else { $null }
    expectedInitialTheftPolicy = if ([string]::IsNullOrWhiteSpace($ExpectedInitialTheftPolicy)) { $null } else { $ExpectedInitialTheftPolicy }
    expectedInitialOutstandingStolenResource = if ($ExpectedInitialOutstandingStolenResource -ge 0) { $ExpectedInitialOutstandingStolenResource } else { $null }
    expectedInitialSearchedTurnsAtLeast = if ($ExpectedInitialSearchedTurnsAtLeast -ge 0) { $ExpectedInitialSearchedTurnsAtLeast } else { $null }
    expectedInitialShufflesCrossedAtLeast = if ($ExpectedInitialShufflesCrossedAtLeast -ge 0) { $ExpectedInitialShufflesCrossedAtLeast } else { $null }
    expectedInitialUnmirroredCount = if ($ExpectedInitialUnmirroredCount -ge 0) { $ExpectedInitialUnmirroredCount } else { $null }
    expectedInitialHpLostAtMost = if ($ExpectedInitialHpLostAtMost -ge 0) { $ExpectedInitialHpLostAtMost } else { $null }
    expectedInitialProjectedBattleHpLost = if ($ExpectedInitialProjectedBattleHpLost -ge 0) { $ExpectedInitialProjectedBattleHpLost } else { $null }
    expectedInitialProjectedBattleHpLostAtMost = if ($ExpectedInitialProjectedBattleHpLostAtMost -ge 0) { $ExpectedInitialProjectedBattleHpLostAtMost } else { $null }
    expectedInitialLongTermResourceValueAtLeast = if ($ExpectedInitialLongTermResourceValueAtLeast -ge 0) { $ExpectedInitialLongTermResourceValueAtLeast } else { $null }
    expectedInitialFinalMaxHp = if ($ExpectedInitialFinalMaxHp -ge 0) { $ExpectedInitialFinalMaxHp } else { $null }
    expectedInitialMaxBlockAtLeast = if ($ExpectedInitialMaxBlockAtLeast -ge 0) { $ExpectedInitialMaxBlockAtLeast } else { $null }
    expectedInitialActualBlockAtLeast = if ($ExpectedInitialActualBlockAtLeast -ge 0) { $ExpectedInitialActualBlockAtLeast } else { $null }
    expectedInitialActionCardId = if ([string]::IsNullOrWhiteSpace($ExpectedInitialActionCardId)) { $null } else { $ExpectedInitialActionCardId }
    expectedInitialAbsentActionCardId = if ([string]::IsNullOrWhiteSpace($ExpectedInitialAbsentActionCardId)) { $null } else { $ExpectedInitialAbsentActionCardId }
    expectedInitialFirstActionCardId = if ([string]::IsNullOrWhiteSpace($ExpectedInitialFirstActionCardId)) { $null } else { $ExpectedInitialFirstActionCardId }
    expectedInitialFirstActionPotionId = if ([string]::IsNullOrWhiteSpace($ExpectedInitialFirstActionPotionId)) { $null } else { $ExpectedInitialFirstActionPotionId }
    expectedInitialActionTitle = if ([string]::IsNullOrWhiteSpace($ExpectedInitialActionTitle)) { $null } else { $ExpectedInitialActionTitle }
    expectedInitialActionReplayCount = if ($ExpectedInitialActionReplayCount -ge 0) { $ExpectedInitialActionReplayCount } else { $null }
    expectedInitialOnlyDeathRoutesFound = if ($ExpectedInitialOnlyDeathRoutesFound -ge 0) { [bool]$ExpectedInitialOnlyDeathRoutesFound } else { $null }
    expectedInitialCombatEndedTurn = if ($ExpectedInitialCombatEndedTurn -gt 0) { $ExpectedInitialCombatEndedTurn } else { $null }
    expectedInitialDeathTurn = if ($ExpectedInitialDeathTurn -gt 0) { $ExpectedInitialDeathTurn } else { $null }
    expectedInitialDeathTurnAtLeast = if ($ExpectedInitialDeathTurnAtLeast -gt 0) { $ExpectedInitialDeathTurnAtLeast } else { $null }
    expectedInitialFinalEnemyHpAtMost = if ($ExpectedInitialFinalEnemyHpAtMost -ge 0) { $ExpectedInitialFinalEnemyHpAtMost } else { $null }
    expectedInitialActEndingBoss = if ($ExpectedInitialActEndingBoss -ge 0) { [bool]$ExpectedInitialActEndingBoss } else { $null }
    expectedInitialBossHpRelief = if ([string]::IsNullOrWhiteSpace($ExpectedInitialBossHpRelief)) { $null } else { $ExpectedInitialBossHpRelief }
    expectedInitialPlannedChoiceCardId = if ([string]::IsNullOrWhiteSpace($ExpectedInitialPlannedChoiceCardId)) { $null } else { $ExpectedInitialPlannedChoiceCardId }
    expectedInitialTurnStartChoiceTurn = if ($ExpectedInitialTurnStartChoiceTurn -gt 0) { $ExpectedInitialTurnStartChoiceTurn } else { $null }
    expectedInitialTurnStartChoiceSourceId = if ([string]::IsNullOrWhiteSpace($ExpectedInitialTurnStartChoiceSourceId)) { $null } else { $ExpectedInitialTurnStartChoiceSourceId }
    expectedInitialTurnStartChoiceCardId = if ([string]::IsNullOrWhiteSpace($ExpectedInitialTurnStartChoiceCardId)) { $null } else { $ExpectedInitialTurnStartChoiceCardId }
    expectedInitialTurnStartChoiceStateContains = if ([string]::IsNullOrWhiteSpace($ExpectedInitialTurnStartChoiceStateContains)) { $null } else { $ExpectedInitialTurnStartChoiceStateContains }
    expectedInitialTurnStartChoiceStateExcludes = if ([string]::IsNullOrWhiteSpace($ExpectedInitialTurnStartChoiceStateExcludes)) { $null } else { $ExpectedInitialTurnStartChoiceStateExcludes }
    expectedInitialSetupChoiceCountAtLeast = if ($ExpectedInitialSetupChoiceCountAtLeast -ge 0) { $ExpectedInitialSetupChoiceCountAtLeast } else { $null }
    expectedInitialSetupChoiceSourceId = if ([string]::IsNullOrWhiteSpace($ExpectedInitialSetupChoiceSourceId)) { $null } else { $ExpectedInitialSetupChoiceSourceId }
    expectedInitialSetupChoiceTextStartsWith = if ([string]::IsNullOrWhiteSpace($ExpectedInitialSetupChoiceTextStartsWith)) { $null } else { $ExpectedInitialSetupChoiceTextStartsWith }
    verifyInitialSetupWaitsForUserStart = $VerifyInitialSetupWaitsForUserStart.IsPresent
    verifyTurnSetupManualRecalculate = $VerifyTurnSetupManualRecalculate.IsPresent
    verifyTurnSetupManualRefresh = $VerifyTurnSetupManualRefresh.IsPresent
    verifyTurnSetupControlsDuringInitialSearch = $VerifyTurnSetupControlsDuringInitialSearch.IsPresent
    verifyTurnSetupSceneExitCancellation = $VerifyTurnSetupSceneExitCancellation.IsPresent
    stopAfterInitialSetupAssertion = $StopAfterInitialSetupAssertion.IsPresent
    stopAfterInitialSolverResultAssertion = $StopAfterInitialSolverResultAssertion.IsPresent
    expectedFullAutoPausedAtDeathTurn = $ExpectedFullAutoPausedAtDeathTurn.IsPresent
    expectedFullAutoPausedAfterWorseRecalculation = $ExpectedFullAutoPausedAfterWorseRecalculation.IsPresent
    expectedFullAutoPausedAtLiveRisk = $ExpectedFullAutoPausedAtLiveRisk.IsPresent
    enableStopOnWorseRecalculationForTest = $EnableStopOnWorseRecalculationForTest.IsPresent
    expectedInitialRelicEffectId = if ([string]::IsNullOrWhiteSpace($ExpectedInitialRelicEffectId)) { $null } else { $ExpectedInitialRelicEffectId }
    expectedInitialRelicEffectSummary = if ([string]::IsNullOrWhiteSpace($ExpectedInitialRelicEffectSummary)) { $null } else { $ExpectedInitialRelicEffectSummary }
    expectedReusedTurn = if ($ExpectedReusedTurn -gt 0) { $ExpectedReusedTurn } else { $null }
    expectedReusedProjectedBattleHpLost = if ($ExpectedReusedProjectedBattleHpLost -ge 0) { $ExpectedReusedProjectedBattleHpLost } else { $null }
    expectedUnexpectedReplansAtMost = if ($ExpectedUnexpectedReplansAtMost -ge 0) { $ExpectedUnexpectedReplansAtMost } else { $null }
    stopAfterExpectedReuse = $StopAfterExpectedReuse.IsPresent
    expectedPlayedCardId = if ([string]::IsNullOrWhiteSpace($ExpectedPlayedCardId)) { $null } else { $ExpectedPlayedCardId }
    expectedUsedPotionId = if ([string]::IsNullOrWhiteSpace($ExpectedUsedPotionId)) { $null } else { $ExpectedUsedPotionId }
    expectedObservedPlayerPowerId = if ([string]::IsNullOrWhiteSpace($ExpectedObservedPlayerPowerId)) { $null } else { $ExpectedObservedPlayerPowerId }
    expectedNativeChoiceOwnerPrefix = if ([string]::IsNullOrWhiteSpace($ExpectedNativeChoiceOwnerPrefix)) { $null } else { $ExpectedNativeChoiceOwnerPrefix }
    expectedNativeChoiceSurface = if ([string]::IsNullOrWhiteSpace($ExpectedNativeChoiceSurface)) { $null } else { $ExpectedNativeChoiceSurface }
    expectedNativeChoiceVisibleAtLeast = if ($ExpectedNativeChoiceVisibleAtLeast -ge 0) { $ExpectedNativeChoiceVisibleAtLeast } else { $null }
    expectedNativeChoiceSearchStartedAtMost = if ($ExpectedNativeChoiceSearchStartedAtMost -ge 0) { $ExpectedNativeChoiceSearchStartedAtMost } else { $null }
    stopAfterExpectedPlayerPower = $StopAfterExpectedPlayerPower.IsPresent
    expectedPlayerDeath = $ExpectedPlayerDeath.IsPresent
    headlessFastModeForTest = if ([string]::IsNullOrWhiteSpace($HeadlessFastModeForTest)) { $null } else { $HeadlessFastModeForTest }
    deploymentFastModeForTest = if ([string]::IsNullOrWhiteSpace($DeploymentFastModeForTest)) { $null } else { $DeploymentFastModeForTest }
    performancePresetForTest = if ([string]::IsNullOrWhiteSpace($PerformancePresetForTest)) { $null } else { $PerformancePresetForTest }
    shortMaxCardBranchesPerNodeForTest = if ($ShortMaxCardBranchesPerNodeForTest -gt 0) { $ShortMaxCardBranchesPerNodeForTest } else { $null }
    deepMaxCardBranchesPerNodeForTest = if ($DeepMaxCardBranchesPerNodeForTest -gt 0) { $DeepMaxCardBranchesPerNodeForTest } else { $null }
    potionPolicyForTest = if ([string]::IsNullOrWhiteSpace($PotionPolicyForTest)) { $null } else { $PotionPolicyForTest }
    potionDirectivesForTest = if ([string]::IsNullOrWhiteSpace($PotionDirectivesForTestJson)) { $null } else { @($PotionDirectivesForTestJson | ConvertFrom-Json) }
    actTransitionBossHpStrategyForTest = if ([string]::IsNullOrWhiteSpace($ActTransitionBossHpStrategyForTest)) { $null } else { $ActTransitionBossHpStrategyForTest }
    finalBossHpStrategyForTest = if ([string]::IsNullOrWhiteSpace($FinalBossHpStrategyForTest)) { $null } else { $FinalBossHpStrategyForTest }
    theftPolicyForTest = if ([string]::IsNullOrWhiteSpace($TheftPolicyForTest)) { $null } else { $TheftPolicyForTest }
    enableNoGcRegionForTest = if ($EnableNoGcRegionForTest -ge 0) { [bool]$EnableNoGcRegionForTest } else { $null }
    noGcRegionBudgetGigabytesForTest = if ($NoGcRegionBudgetGigabytesForTest -gt 0) { $NoGcRegionBudgetGigabytesForTest } else { $null }
    deploymentInterActionDelaySecondsForTest = if ($DeploymentInterActionDelaySecondsForTest -ge 0) { $DeploymentInterActionDelaySecondsForTest } else { $null }
    assertDeploymentSpeedRestored = $AssertDeploymentSpeedRestored.IsPresent
    exportBugReportAfterSetup = $ExportBugReportAfterSetup.IsPresent
    exportBugReportAfterCombat = $ExportBugReportAfterCombat.IsPresent
    enableDetailedDiagnosticLogsForTest = if ($EnableDetailedDiagnosticLogsForTest -ge 0) { [bool]$EnableDetailedDiagnosticLogsForTest } else { $null }
    manualEndTurnAfterInitialSearch = $ManualEndTurnAfterInitialSearch.IsPresent
    singleStepAfterInitialSearch = $SingleStepAfterInitialSearch.IsPresent
    singleStepResumeModeForTest = if ([string]::IsNullOrWhiteSpace($SingleStepResumeModeForTest)) { $null } else { $SingleStepResumeModeForTest }
    expectedTurnSetupToDeploymentDelayMillisecondsAtLeast = if ($ExpectedTurnSetupToDeploymentDelayMillisecondsAtLeast -ge 0) { $ExpectedTurnSetupToDeploymentDelayMillisecondsAtLeast } else { $null }
    enableFullAutoAfterManualEndTurn = $EnableFullAutoAfterManualEndTurn.IsPresent
    expectedManualDivergencesAtLeast = if ($ExpectedManualDivergencesAtLeast -ge 0) { $ExpectedManualDivergencesAtLeast } else { $null }
    expectedUnexpectedReplansAtLeast = if ($ExpectedUnexpectedReplansAtLeast -ge 0) { $ExpectedUnexpectedReplansAtLeast } else { $null }
    stopAfterExpectedUnexpectedReplan = $StopAfterExpectedUnexpectedReplan.IsPresent
    expectedUnexpectedReplanWarning = $ExpectedUnexpectedReplanWarning.IsPresent
    exportBugReportAfterUnexpectedReplan = $ExportBugReportAfterUnexpectedReplan.IsPresent
    expectedBugReportControlMode = if ([string]::IsNullOrWhiteSpace($ExpectedBugReportControlMode)) { $null } else { $ExpectedBugReportControlMode }
    expectedNoGcRegionRolloversAtLeast = if ($ExpectedNoGcRegionRolloversAtLeast -ge 0) { $ExpectedNoGcRegionRolloversAtLeast } else { $null }
    injectPlayerHpLossBeforeAutoSearchTurn = if ($InjectPlayerHpLossBeforeAutoSearchTurn -gt 0) { $InjectPlayerHpLossBeforeAutoSearchTurn } else { $null }
    injectPlayerHpLossAmount = $InjectPlayerHpLossAmount
    clearPlayerBlockBeforeEndTurnForTest = if ($ClearPlayerBlockBeforeEndTurnForTest -gt 0) { $ClearPlayerBlockBeforeEndTurnForTest } else { $null }
    exitOnComplete = $ExitOnComplete.IsPresent
}
if (-not [string]::IsNullOrWhiteSpace($InitialEnemyCurrentHpsJson)) {
    $request.initialEnemyCurrentHps = @($InitialEnemyCurrentHpsJson | ConvertFrom-Json)
}
if (-not [string]::IsNullOrWhiteSpace($InitialEnemyMoveIdsJson)) {
    $request.initialEnemyMoveIds = @($InitialEnemyMoveIdsJson | ConvertFrom-Json)
}
if (-not [string]::IsNullOrWhiteSpace($OrbsJson)) {
    $request.orbs = @($OrbsJson | ConvertFrom-Json)
}
if (-not [string]::IsNullOrWhiteSpace($PowersPath)) {
    $request.powers = @(Get-Content -LiteralPath $PowersPath -Raw | ConvertFrom-Json)
} elseif (-not [string]::IsNullOrWhiteSpace($PowersJson)) {
    $request.powers = @($PowersJson | ConvertFrom-Json)
}
if (-not [string]::IsNullOrWhiteSpace($RelicsPath)) {
    $request.relics = @(Get-Content -LiteralPath $RelicsPath -Raw | ConvertFrom-Json)
} elseif (-not [string]::IsNullOrWhiteSpace($RelicsJson)) {
    $request.relics = @($RelicsJson | ConvertFrom-Json)
}
if (-not [string]::IsNullOrWhiteSpace($CombatRelicsPath)) {
    $request.combatRelics = @(Get-Content -LiteralPath $CombatRelicsPath -Raw | ConvertFrom-Json)
} elseif (-not [string]::IsNullOrWhiteSpace($CombatRelicsJson)) {
    $request.combatRelics = @($CombatRelicsJson | ConvertFrom-Json)
}
if (-not [string]::IsNullOrWhiteSpace($CardsPath)) {
    $request.cards = @(Get-Content -LiteralPath $CardsPath -Raw | ConvertFrom-Json)
} elseif (-not [string]::IsNullOrWhiteSpace($CardsJson)) {
    $request.cards = @($CardsJson | ConvertFrom-Json)
} elseif ($null -ne $replayStateCards) {
    $request.cards = $replayStateCards
}
if (-not [string]::IsNullOrWhiteSpace($RunCardsPath)) {
    $request.runCards = @(Get-Content -LiteralPath $RunCardsPath -Raw | ConvertFrom-Json)
} elseif (-not [string]::IsNullOrWhiteSpace($RunCardsJson)) {
    $request.runCards = @($RunCardsJson | ConvertFrom-Json)
}
if (-not [string]::IsNullOrWhiteSpace($PotionsPath)) {
    $request.potions = @(Get-Content -LiteralPath $PotionsPath -Raw | ConvertFrom-Json)
} elseif (-not [string]::IsNullOrWhiteSpace($PotionsJson)) {
    $request.potions = @($PotionsJson | ConvertFrom-Json)
} elseif (-not [string]::IsNullOrWhiteSpace($PotionId)) {
    $request.potions = @([ordered]@{ potionId = $PotionId })
}
if (-not [string]::IsNullOrWhiteSpace($PotionChecksPath)) {
    $request.potionChecks = @(Get-Content -LiteralPath $PotionChecksPath -Raw | ConvertFrom-Json)
} elseif (-not [string]::IsNullOrWhiteSpace($PotionChecksJson)) {
    $request.potionChecks = @($PotionChecksJson | ConvertFrom-Json)
} elseif (-not [string]::IsNullOrWhiteSpace($PotionCheckPath)) {
    $request.potionCheck = Get-Content -LiteralPath $PotionCheckPath -Raw | ConvertFrom-Json
} elseif (-not [string]::IsNullOrWhiteSpace($PotionCheckJson)) {
    $request.potionCheck = $PotionCheckJson | ConvertFrom-Json
} elseif (-not [string]::IsNullOrWhiteSpace($MonsterMoveChecksPath)) {
    $request.monsterMoveChecks = @(Get-Content -LiteralPath $MonsterMoveChecksPath -Raw | ConvertFrom-Json)
} elseif (-not [string]::IsNullOrWhiteSpace($MonsterMoveChecksJson)) {
    $request.monsterMoveChecks = @($MonsterMoveChecksJson | ConvertFrom-Json)
} elseif ([string]::IsNullOrWhiteSpace($MonsterMoveId)) {
    if ($request.cards.Count -eq 0 -and
        -not $cardsExplicitlyConfigured -and
        [string]::IsNullOrWhiteSpace($RunSnapshotPath)) {
        $request.cards = @(
            [ordered]@{
                cardId = $CardId
                pile = "Hand"
                count = 1
                upgradeLevels = 0
            }
        )
    }
} else {
    $expectedPlayerPowers = $ExpectedPlayerPowersJson | ConvertFrom-Json
    $expectedEnemyPowers = $ExpectedEnemyPowersJson | ConvertFrom-Json
    $request.monsterMoveCheck = [ordered]@{
        enemyIndex = 0
        monsterId = $MonsterId
        moveId = $MonsterMoveId
        expectedPlayerHpLoss = if ($ExpectedPlayerHpLoss -ge 0) { $ExpectedPlayerHpLoss } else { $null }
        expectedEnemyBlockGain = if ($ExpectedEnemyBlockGain -ge 0) { $ExpectedEnemyBlockGain } else { $null }
        expectedPlayerPowers = $expectedPlayerPowers
        expectedEnemyPowers = $expectedEnemyPowers
    }
}
if (-not [string]::IsNullOrWhiteSpace($OrbChecksPath)) {
    $request.orbChecks = @(Get-Content -LiteralPath $OrbChecksPath -Raw | ConvertFrom-Json)
} elseif (-not [string]::IsNullOrWhiteSpace($OrbChecksJson)) {
    $request.orbChecks = @($OrbChecksJson | ConvertFrom-Json)
}
if (-not [string]::IsNullOrWhiteSpace($PowerId)) {
    $request.powers = @(
        [ordered]@{
            powerId = $PowerId
            target = $PowerTarget
            targetIndex = 0
            amount = $PowerAmount
        }
    )
}

$combatSolverDllSha256 = (Get-FileHash -LiteralPath $combatSolverDll -Algorithm SHA256).Hash
$combatSolverManifestSha256 = (Get-FileHash -LiteralPath $combatSolverManifest -Algorithm SHA256).Hash
if (Test-Path -LiteralPath $processMarkerPath -PathType Leaf) {
    $marker = $null
    $markerProcessId = 0
    $markerIsOwned = $false
    $discardMarker = $false
    $markerProblem = "marker contents did not match the isolated headless process"
    try {
        $marker = Get-Content -LiteralPath $processMarkerPath -Raw | ConvertFrom-Json
        if (-not [int]::TryParse([string]$marker.pid, [ref]$markerProcessId) -or
            $markerProcessId -le 0) {
            throw "marker PID is invalid"
        }
        $markerStartTimeUtc = ConvertTo-NormalizedUtcTimestamp $marker.processStartTimeUtc
        if ([string]::IsNullOrWhiteSpace($markerStartTimeUtc)) {
            throw "marker process start time is missing or invalid"
        }
        $markerExecutable = [IO.Path]::GetFullPath([string]$marker.executable)
        $requestedExecutableProperty = $marker.PSObject.Properties['requestedExecutable']
        $markerRequestedExecutable = if ($null -eq $requestedExecutableProperty -or
            [string]::IsNullOrWhiteSpace([string]$requestedExecutableProperty.Value)) {
            $null
        } else {
            [IO.Path]::GetFullPath([string]$requestedExecutableProperty.Value)
        }
        $markerAppData = [IO.Path]::GetFullPath([string]$marker.appData)
        $markerDataDir = [IO.Path]::GetFullPath([string]$marker.dataDir)
        if (($null -ne $markerRequestedExecutable -and
                -not [string]::Equals(
                    $markerRequestedExecutable,
                    $gameExe,
                    [StringComparison]::OrdinalIgnoreCase)) -or
            -not [string]::Equals($markerAppData, $headlessRoaming, [StringComparison]::OrdinalIgnoreCase) -or
            -not [string]::Equals($markerDataDir, $dataDir, [StringComparison]::OrdinalIgnoreCase)) {
            throw "marker paths do not match this isolated launcher"
        }
    } catch {
        $markerProblem = $_.Exception.Message
        if ($markerProcessId -gt 0) {
            $legacyCandidate = Get-Process -Id $markerProcessId -ErrorAction SilentlyContinue
            if ($null -ne $legacyCandidate) {
                try {
                    $legacyCandidateSafeHandle = $legacyCandidate.SafeHandle
                    $legacyCandidate.Refresh()
                    if (-not $legacyCandidate.HasExited -and
                        $legacyCandidate.ProcessName -eq "SlayTheSpire2") {
                        throw "live SlayTheSpire2 process still uses an older or invalid ownership marker"
                    }
                } catch {
                    throw "Could not safely upgrade the existing headless marker; " +
                        "the process, marker, and dependency were preserved. pid=$markerProcessId " +
                        "error=$($_.Exception.Message)"
                }
            }
        } else {
            $unidentifiedGameProcesses = @(Get-Process -Name "SlayTheSpire2" -ErrorAction SilentlyContinue)
            foreach ($unidentifiedGameProcess in $unidentifiedGameProcesses) {
                try {
                    $unidentifiedSafeHandle = $unidentifiedGameProcess.SafeHandle
                    $unidentifiedGameProcess.Refresh()
                    if (-not $unidentifiedGameProcess.HasExited) {
                        throw "a live SlayTheSpire2 process exists but the marker has no usable PID"
                    }
                } catch {
                    throw "Could not safely replace the invalid headless marker; " +
                        "the process, marker, and dependency were preserved. error=$($_.Exception.Message)"
                }
            }
        }
        $discardMarker = $true
    }

    if (-not $discardMarker) {
        $candidate = Get-Process -Id $markerProcessId -ErrorAction SilentlyContinue
        if ($null -eq $candidate) {
            $markerProblem = "marker process is absent"
            $discardMarker = $true
        } else {
            try {
                # Retain this SafeHandle for the whole launcher invocation so a
                # recycled PID can never redirect validation or cleanup.
                $candidateSafeHandle = $candidate.SafeHandle
                if (-not (Test-ProcessMatchesHeadlessIdentity $candidate $markerStartTimeUtc $markerExecutable)) {
                    $markerProblem = "marker process has a different executable or start time"
                    $discardMarker = $true
                } else {
                    $markerIsOwned = $true
                    $process = $candidate
                    $processSafeHandle = $candidateSafeHandle
                    $processIdentityStartTimeUtc = $markerStartTimeUtc
                    $cleanupProcessOnExit = $true
                }
            } catch {
                throw "Could not conclusively validate the managed headless process; " +
                    "its marker and dependency were preserved. pid=$markerProcessId error=$($_.Exception.Message)"
            }
        }
    }

    if ($discardMarker) {
        Write-Warning "Discarding stale or unowned process marker ($markerProblem): $processMarkerPath"
        Remove-Item -LiteralPath $processMarkerPath -Force
    } elseif (-not [string]::Equals(
            [string]$marker.combatSolverDllSha256,
            $combatSolverDllSha256,
            [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            [string]$marker.combatSolverManifestSha256,
            $combatSolverManifestSha256,
            [StringComparison]::OrdinalIgnoreCase)) {
        Write-Host "UNATTENDED_RESTART reason=mod_changed pid=$($process.Id)"
        Stop-ClaimedProcessAndRemoveDependency $process $processIdentityStartTimeUtc
        $cleanupProcessOnExit = $false
        $process = $null
        $processIdentityStartTimeUtc = ""
    } elseif (Test-Path -LiteralPath $readyPath -PathType Leaf) {
        $previousReadyHeld = $false
        try {
            $previousReady = Get-Content -LiteralPath $readyPath -Raw | ConvertFrom-Json
            $previousReadyHeld = $previousReady.schemaVersion -eq 1 -and
                $previousReady.held -eq $true -and
                -not [string]::IsNullOrWhiteSpace([string]$previousReady.runId)
        } catch {
            Write-Warning "Could not inspect the previous ready marker; it will be replaced by this request."
        }
        if ($previousReadyHeld) {
            Write-Host "UNATTENDED_RESTART reason=held_process_not_reusable pid=$($process.Id)"
            Stop-ClaimedProcessAndRemoveDependency $process $processIdentityStartTimeUtc
            $cleanupProcessOnExit = $false
            $process = $null
            $processIdentityStartTimeUtc = ""
        }
    }
}
$otherProcesses = @(Get-Process -Name "SlayTheSpire2" -ErrorAction SilentlyContinue |
    Where-Object { $null -eq $process -or $_.Id -ne $process.Id })
if ($otherProcesses.Count -gt 0) {
    $ids = $otherProcesses.Id -join ","
    throw "Refusing to start or reuse headless tests while an interactive SlayTheSpire2 process is running. pid=$ids"
}
$reusedProcess = $null -ne $process
Assert-LauncherNotCancelled

# Publish only after marker ownership, process identity, mod fingerprint, and
# interactive-process checks have all succeeded.
$requestTempPath = "$requestPath.$runId.tmp"
if (Test-Path -LiteralPath $readyPath -PathType Leaf) {
    Remove-Item -LiteralPath $readyPath -Force
}
$request | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $requestTempPath -Encoding UTF8
Move-Item -LiteralPath $requestTempPath -Destination $requestPath -Force
$startedAt = Get-Date
if ($reusedProcess) {
    $cleanupProcessOnExit = $true
}

if (-not $reusedProcess) {
    Install-HeadlessDependency
    $arguments = "--headless --disable-vsync --max-fps 0 --force-steam=off --log-file `"$headlessLogPath`""
    try {
        Assert-LauncherNotCancelled
        $process = Start-Process `
            -FilePath $gameExe `
            -ArgumentList $arguments `
            -Environment @{
                APPDATA = $headlessRoaming
                LOCALAPPDATA = $headlessLocal
                COMBATSOLVER_HEADLESS = "1"
            } `
            -WindowStyle Hidden `
            -PassThru
        # Start-Process returned this exact Process object, so the launcher owns
        # its handle even before StartTime is readable and marker identity exists.
        $startedHere = $true
        $cleanupProcessOnExit = $true
        $processSafeHandle = $process.SafeHandle
        $processIdentityStartTimeUtc = Get-ProcessStartTimeUtc $process
        Assert-LauncherNotCancelled
        $process.Refresh()
        if ($process.HasExited -or $process.ProcessName -ne "SlayTheSpire2") {
            throw "Started process exited or did not expose the expected game process."
        }
        $processActualExecutable = Get-ProcessExecutablePath $process
    } catch {
        $launchError = $_
        if ($startedHere) {
            try {
                Stop-ClaimedProcessAndRemoveDependency $process $processIdentityStartTimeUtc
                $startedHere = $false
                $cleanupProcessOnExit = $false
            } catch {
                throw "Headless game startup failed: $($launchError.Exception.Message) Cleanup also failed: $($_.Exception.Message)"
            }
        } else {
            try {
                Remove-HeadlessDependency
            } catch {
                throw "Headless game startup failed: $($launchError.Exception.Message) " +
                    "Dependency cleanup also failed: $($_.Exception.Message)"
            }
        }
        throw $launchError
    }
    $processMarkerTempPath = "$processMarkerPath.$runId.tmp"
    [ordered]@{
        pid = $process.Id
        startedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
        processStartTimeUtc = $processIdentityStartTimeUtc
        requestedExecutable = $gameExe
        executable = $processActualExecutable
        appData = $headlessRoaming
        dataDir = $dataDir
        logPath = $headlessLogPath
        combatSolverDllSha256 = $combatSolverDllSha256
        combatSolverManifestSha256 = $combatSolverManifestSha256
    } | ConvertTo-Json | Set-Content -LiteralPath $processMarkerTempPath -Encoding UTF8
    Move-Item -LiteralPath $processMarkerTempPath -Destination $processMarkerPath -Force
    $launchDeadline = $startedAt.AddSeconds(30)
    while (-not $process.HasExited -and (Get-Date) -lt $launchDeadline) {
        if (Test-Path -LiteralPath $headlessLogPath -PathType Leaf) {
            break
        }
        Start-Sleep -Milliseconds 250
        Assert-LauncherNotCancelled
        $process.Refresh()
    }
}
if ($null -eq $process -or $process.HasExited) {
    Stop-ClaimedProcessAndRemoveDependency $process $processIdentityStartTimeUtc
    throw "Headless SlayTheSpire2 did not remain running. log=$headlessLogPath"
}
if ($reusedProcess) {
    Write-Host "UNATTENDED_REUSED run_id=$runId pid=$($process.Id)"
} else {
    Write-Host "UNATTENDED_STARTED run_id=$runId pid=$($process.Id)"
}

$resultDeadline = $startedAt.AddSeconds($TimeoutSeconds + 45)
while ((Get-Date) -lt $resultDeadline) {
    Assert-LauncherNotCancelled
    if (Test-Path -LiteralPath $resultPath) {
        $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
        if ($result.runId -eq $runId) {
            $result | ConvertTo-Json -Depth 8
            if ($result.status -ne "Passed") {
                Stop-ClaimedProcessAndRemoveDependency $process $processIdentityStartTimeUtc
                $cleanupProcessOnExit = $false
                exit 1
            }
            $quiescenceDeadline = (Get-Date).AddSeconds(120)
            if ($HoldAfterInitialSearch.IsPresent -and $result.status -eq "Passed") {
                $ready = $null
                while (-not $process.HasExited -and (Get-Date) -lt $quiescenceDeadline) {
                    if (Test-Path -LiteralPath $readyPath -PathType Leaf) {
                        $ready = Get-Content -LiteralPath $readyPath -Raw | ConvertFrom-Json
                        if ($ready.schemaVersion -eq 1 -and
                            $ready.runId -eq $runId -and
                            $ready.held -eq $true) {
                            break
                        }
                    }
                    Start-Sleep -Milliseconds 100
                    Assert-LauncherNotCancelled
                    $process.Refresh()
                }
                if ($process.HasExited -or
                    $null -eq $ready -or
                    $ready.schemaVersion -ne 1 -or
                    $ready.runId -ne $runId -or
                    $ready.held -ne $true) {
                    Stop-ClaimedProcessAndRemoveDependency $process $processIdentityStartTimeUtc
                    $cleanupProcessOnExit = $false
                    throw "Held test did not reach quiescence before timeout. run_id=$runId"
                }
                Write-Host "UNATTENDED_HELD run_id=$runId pid=$($process.Id) release=$holdReleasePath"
                while (-not $process.HasExited -and
                    -not (Test-Path -LiteralPath $holdReleasePath -PathType Leaf)) {
                    Start-Sleep -Milliseconds 500
                    Assert-LauncherNotCancelled
                    $process.Refresh()
                }
                if (-not (Test-Path -LiteralPath $holdReleasePath -PathType Leaf)) {
                    Stop-ClaimedProcessAndRemoveDependency $process $processIdentityStartTimeUtc
                    $cleanupProcessOnExit = $false
                    throw "Held test process exited before the release marker was written. run_id=$runId"
                }
                Assert-LauncherNotCancelled
                Remove-Item -LiteralPath $holdReleasePath -Force
                Stop-ClaimedProcessAndRemoveDependency $process $processIdentityStartTimeUtc
                $cleanupProcessOnExit = $false
                exit 0
            }
            if ($ExitOnComplete.IsPresent) {
                $exitDeadline = (Get-Date).AddSeconds(30)
                while (-not $process.HasExited -and (Get-Date) -lt $exitDeadline) {
                    $process.WaitForExit(100) | Out-Null
                    Assert-LauncherNotCancelled
                    $process.Refresh()
                }
                Assert-LauncherNotCancelled
                Stop-ClaimedProcessAndRemoveDependency $process $processIdentityStartTimeUtc
                $cleanupProcessOnExit = $false
                exit 0
            }
            $ready = $null
            while (-not $process.HasExited -and (Get-Date) -lt $quiescenceDeadline) {
                if (Test-Path -LiteralPath $readyPath -PathType Leaf) {
                    $ready = Get-Content -LiteralPath $readyPath -Raw | ConvertFrom-Json
                    if ($ready.schemaVersion -eq 1 -and
                        $ready.runId -eq $runId -and
                        $ready.held -eq $false) {
                        Assert-LauncherNotCancelled
                        Write-Host "UNATTENDED_READY run_id=$runId pid=$($process.Id)"
                        $cleanupProcessOnExit = $false
                        exit 0
                    }
                }
                Start-Sleep -Milliseconds 100
                Assert-LauncherNotCancelled
                $process.Refresh()
            }
            Stop-ClaimedProcessAndRemoveDependency $process $processIdentityStartTimeUtc
            $cleanupProcessOnExit = $false
            throw "Test passed but did not become reusable before timeout. run_id=$runId"
        }
    }
    if ($process.HasExited) {
        $processExitCode = "unknown"
        try {
            if ($process.HasExited) {
                $processExitCode = $process.ExitCode
            }
        } catch {
            $processExitCode = "unknown"
        }
        Stop-ClaimedProcessAndRemoveDependency $process $processIdentityStartTimeUtc
        $cleanupProcessOnExit = $false
        throw "Game exited without writing a result for this run. exit_code=$processExitCode"
    }
    Start-Sleep -Milliseconds 500
    Assert-LauncherNotCancelled
    $process.Refresh()
}

Stop-ClaimedProcessAndRemoveDependency $process $processIdentityStartTimeUtc
$cleanupProcessOnExit = $false
throw "Unattended test exceeded the launcher timeout; its game process was stopped. run_id=$runId"
} catch {
    $launcherFailure = $_
    $launcherWasCancelled =
        $_.Exception -is [OperationCanceledException] -or
        [CombatSolverUnattendedLauncherCancellation]::IsCancellationRequested
} finally {
    try {
        if ($cleanupProcessOnExit) {
            try {
                Stop-ClaimedProcessAndRemoveDependency $process $processIdentityStartTimeUtc
            } catch {
                Write-Warning "Failed to clean up the owned headless process during launcher shutdown: $($_.Exception.Message)"
            }
        }
    } finally {
        if ($launcherCancellationInstalled) {
            [CombatSolverUnattendedLauncherCancellation]::Uninstall()
        }
        $launcherLock.Dispose()
    }
}

if ($null -ne $launcherFailure) {
    if ($launcherWasCancelled) {
        Write-Error -ErrorAction Continue $launcherFailure
        exit 130
    }
    throw $launcherFailure
}
