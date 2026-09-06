#requires -Version 7.0

param(
    [int]$TimeoutSeconds = 360,
    [ValidateRange(1, 16)]
    [int]$SearchMaxDegreeOfParallelism = 2,
    [switch]$VerifyBaseLibCardModifierBoundary,
    [switch]$LoggingFixture,
    [string]$EvidenceDirectory,
    [string]$CheckpointArchivePath,
    [string]$ReplayPolicyOverridePath,
    [ValidateSet('RestoreOnly','ReplayRecorded','SearchOnly','DeploySolver')][string]$ReplayMode = 'RestoreOnly',
    [string]$CheckpointSelector = 'latest'
)

$ErrorActionPreference = "Stop"
if ($LoggingFixture) { $TimeoutSeconds = [Math]::Min($TimeoutSeconds, 120) }
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$steamExe = "D:\Steam\steam.exe"
$gameProcessName = "SlayTheSpire2"
$dataDir = Join-Path ([Environment]::GetFolderPath("ApplicationData")) "SlayTheSpire2"
$requestPath = Join-Path $dataDir "combat_solver_test_request.json"
$resultPath = Join-Path $dataDir "combat_solver_test_result.json"
$runSnapshotPath = Join-Path $repositoryRoot "coverage\unattended\mecha-knight-memory-run-snapshot.json"

if (-not (Test-Path -LiteralPath $steamExe -PathType Leaf)) {
    throw "Steam executable not found: $steamExe"
}
if (-not (Test-Path -LiteralPath $runSnapshotPath -PathType Leaf)) {
    throw "Mecha Knight benchmark snapshot not found: $runSnapshotPath"
}
if (Get-Process -Name $gameProcessName -ErrorAction SilentlyContinue) {
    throw "Refusing to start the benchmark while Slay the Spire 2 is already running."
}

$steam = Get-Process -Name "steam" -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -eq $steam) {
    Start-Process -FilePath $steamExe | Out-Null
    $steamDeadline = (Get-Date).AddSeconds(30)
    do {
        Start-Sleep -Milliseconds 250
        $steam = Get-Process -Name "steam" -ErrorAction SilentlyContinue | Select-Object -First 1
    } while ($null -eq $steam -and (Get-Date) -lt $steamDeadline)
    if ($null -eq $steam) {
        throw "Steam did not start within 30 seconds."
    }
}

New-Item -ItemType Directory -Path $dataDir -Force | Out-Null
$requestBackup = if (Test-Path -LiteralPath $requestPath -PathType Leaf) {
    [IO.File]::ReadAllBytes($requestPath)
} else {
    $null
}
$resultBackup = if (Test-Path -LiteralPath $resultPath -PathType Leaf) {
    [IO.File]::ReadAllBytes($resultPath)
} else {
    $null
}

$runId = [Guid]::NewGuid().ToString("N")
$requestTempPath = "$requestPath.$runId.tmp"
$request = [ordered]@{
    schemaVersion = 1
    runId = $runId
    scenarioId = "MECHA-NO-RESCAN-247"
    characterId = "SILENT"
    encounterId = "MECHA_KNIGHT_ELITE"
    runSnapshotPath = (Resolve-Path -LiteralPath $runSnapshotPath).Path
    seed = "BJCZX3J13PZJ"
    ascension = 0
    enemyCurrentHp = 300
    initialPlayerHp = 65
    cards = @()
    powers = @()
    orbs = @()
    relics = @()
    combatRelics = @()
    potions = @()
    potionCheck = $null
    monsterMoveCheck = $null
    monsterMoveChecks = @()
    additionalMonsterIds = @()
    initialEnemyMoveIds = @()
    timeoutSeconds = $TimeoutSeconds
    expectedFinishedTurn = 7
    expectedFinishedTurnAtMost = $null
    clearPlayerHand = $false
    clearPlayerPiles = $false
    verifyIncrementalSearch = $false
    verifyBaseLibCardModifierBoundary = $VerifyBaseLibCardModifierBoundary.IsPresent
    performancePresetForTest = "Medium"
    enableNoGcRegionForTest = $true
    noGcRegionBudgetGigabytesForTest = 16
    deploymentFastModeForTest = "Instant"
    deploymentInterActionDelaySecondsForTest = 0
    assertDeploymentSpeedRestored = $true
    forceShortSearchOnly = $false
    measureSearchPhases = $true
    searchMaxDegreeOfParallelismForTest = $SearchMaxDegreeOfParallelism
    holdAfterInitialSearch = $false
    shortSearchBudgetOverrideMilliseconds = 5000
    deepSearchBudgetOverrideMilliseconds = 60000
    expectedInitialSearchPhase = "Deep"
    expectedInitialDeepSearchTriggered = $true
    expectedInitialDeepSearchImprovedResult = $null
    expectedInitialTotalElapsedMillisecondsAtMost = 20000
    expectedInitialTotalAllocatedBytesAtMost = 5500000000
    expectedInitialGen2CollectionsAtMost = 20
    expectedInitialTotalGcPauseMillisecondsAtMost = 8000
    expectedInitialMaxGcPauseMillisecondsAtMost = 50
    expectedInitialMaxMainThreadFrameGapMillisecondsAtMost = 100
    expectedInitialMainThreadFramesOver50MillisecondsAtMost = 5
    expectedInitialMainThreadFramesOver100MillisecondsAtMost = 0
    expectedInitialTransitionCacheHitsAtLeast = $null
    expectedInitialExecutableActionCountAtLeast = $null
    expectedInitialSoldHp = 0
    expectedInitialSoldHpAtMost = $null
    expectedInitialSoldHpBranchesPrunedAtLeast = $null
    expectedInitialPotionCount = 0
    expectedInitialPotionHpSavedAtLeast = $null
    expectedInitialPotionBranchesRejectedAtLeast = $null
    expectedInitialSearchedTurnsAtLeast = 7
    expectedInitialShufflesCrossedAtLeast = $null
    expectedInitialUnmirroredCount = $null
    expectedInitialHpLostAtMost = $null
    expectedInitialProjectedBattleHpLostAtMost = 43
    expectedInitialMaxBlockAtLeast = $null
    expectedInitialActualBlockAtLeast = $null
    expectedInitialActionCardId = $null
    expectedInitialActionTitle = $null
    expectedReusedTurn = 3
    expectedUnexpectedReplansAtMost = 0
    expectedNativeChoiceOwnerPrefix = "turn_setup:"
    expectedNativeChoiceSurface = "Hand"
    expectedNativeChoiceVisibleAtLeast = 1
    expectedNativeChoiceSearchStartedAtMost = 0
    expectedPlayedCardId = $null
    expectedUsedPotionId = $null
    exitOnComplete = $true
}

$gameProcess = $null
if ($LoggingFixture) {
    $request = Get-Content -LiteralPath (Join-Path $repositoryRoot 'coverage/unattended/logging-short-combat.json') -Raw | ConvertFrom-Json -AsHashtable
    $request.runId = $runId
    $request.timeoutSeconds = $TimeoutSeconds
}
if ($EvidenceDirectory) { $request.evidenceDirectory = [IO.Path]::GetFullPath($EvidenceDirectory) }
if ($CheckpointArchivePath) {
    if (!$PSBoundParameters.ContainsKey('TimeoutSeconds')) { $TimeoutSeconds = 120 }
    $request = @{
        schemaVersion = 1; runId = $runId; scenarioId = 'VISIBLE-CHECKPOINT-REPLAY'
        checkpointArchivePath = (Resolve-Path -LiteralPath $CheckpointArchivePath).Path
        checkpointSelector = $CheckpointSelector; replayMode = $ReplayMode
        timeoutSeconds = $TimeoutSeconds; exitOnComplete = $true
        replayPolicyOverridePath = if ($ReplayPolicyOverridePath) { (Resolve-Path -LiteralPath $ReplayPolicyOverridePath).Path } else { $null }
        evidenceDirectory = if ($EvidenceDirectory) { [IO.Path]::GetFullPath($EvidenceDirectory) } else { $null }
    }
}
try {
    $request | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $requestTempPath -Encoding UTF8
    Move-Item -LiteralPath $requestTempPath -Destination $requestPath -Force

    Start-Process -FilePath $steamExe -ArgumentList @("-applaunch", "2868840") | Out-Null
    $launchDeadline = (Get-Date).AddSeconds(60)
    do {
        Start-Sleep -Milliseconds 250
        $gameProcess = Get-Process -Name $gameProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
    } while ($null -eq $gameProcess -and (Get-Date) -lt $launchDeadline)
    if ($null -eq $gameProcess) {
        throw "Steam did not launch Slay the Spire 2 within 60 seconds."
    }

    Write-Output "VISIBLE_STEAM_STARTED run_id=$runId pid=$($gameProcess.Id)"
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $result = $null
    while ((Get-Date) -lt $deadline) {
        if (Test-Path -LiteralPath $resultPath -PathType Leaf) {
            try {
                $candidate = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
                if ($candidate.runId -eq $runId) {
                    $result = $candidate
                    break
                }
            } catch {
                # The result file is published atomically, but antivirus/indexers can still race an open.
            }
        }
        $gameProcess.Refresh()
        if ($gameProcess.HasExited) {
            throw "The Steam game process exited before publishing benchmark result $runId."
        }
        Start-Sleep -Milliseconds 250
    }
    if ($null -eq $result) {
        throw "Visible Steam benchmark timed out after $TimeoutSeconds seconds."
    }

    $result | ConvertTo-Json -Depth 8
    if ($result.status -ne "Passed") {
        throw "Visible Steam benchmark failed at stage '$($result.stage)': $($result.error)"
    }

    $exitDeadline = (Get-Date).AddSeconds(30)
    do {
        $gameProcess.Refresh()
        if ($gameProcess.HasExited) {
            break
        }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $exitDeadline)
} finally {
    if ($null -ne $gameProcess) {
        $gameProcess.Refresh()
        if (-not $gameProcess.HasExited) {
            [void]$gameProcess.CloseMainWindow()
            if (-not $gameProcess.WaitForExit(15000)) {
                Stop-Process -Id $gameProcess.Id -Force
                $gameProcess.WaitForExit(10000) | Out-Null
            }
        }
    }
    if (Test-Path -LiteralPath $requestTempPath -PathType Leaf) {
        Remove-Item -LiteralPath $requestTempPath -Force
    }
    if ($null -ne $requestBackup) {
        [IO.File]::WriteAllBytes($requestPath, $requestBackup)
    } elseif (Test-Path -LiteralPath $requestPath -PathType Leaf) {
        Remove-Item -LiteralPath $requestPath -Force
    }
    if ($null -ne $resultBackup) {
        [IO.File]::WriteAllBytes($resultPath, $resultBackup)
    } elseif (Test-Path -LiteralPath $resultPath -PathType Leaf) {
        Remove-Item -LiteralPath $resultPath -Force
    }
}
