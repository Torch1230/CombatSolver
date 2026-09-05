<#
.SYNOPSIS
批量回放 CombatSolver 报告：默认使用 Very High，单包失败后继续下一包；High 仅用于专项诊断。

.EXAMPLE
./tools/run-strategy-replay-batch.ps1 -ReportsRoot ./.local/issue-bundles/better-worldline-20260831/raw/reports -ManifestPath ./manifest.json

.EXAMPLE
./tools/run-strategy-replay-batch.ps1 -ReportsRoot ./reports -PreflightOnly
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ReportsRoot,

    [string]$ManifestPath,
    [string]$WorkbookPath,
    [string]$WorkbookReaderScript = ".local/strategy-batch/read-noted-sheet.mjs",
    [string]$NodePath = "node",
    [string]$OutputDirectory,
    [string[]]$ReportId,
    [string[]]$ExcludeReportId = @("52911e5d91de488aa8a7f51512314bf4"),
    [ValidateRange(1, 16)]
    [int]$SearchParallelism = 4,
    [ValidateSet("Disabled", "Smart", "RequireAtLeastOne")]
    [string]$PotionPolicy = "Smart",
    [ValidateSet("Reported", "ProgressionFirst", "MinimizeHpLoss")]
    [string]$BossHpStrategy = "Reported",
    [ValidateRange(10, 3600)]
    [int]$HighTimeoutSeconds = 120,
    [ValidateRange(10, 3600)]
    [int]$VeryHighTimeoutSeconds = 120,
    [ValidateRange(0, 3600000)]
    [int]$ShortBudgetOverrideMilliseconds = 0,
    [ValidateRange(0, 3600000)]
    [int]$DeepBudgetOverrideMilliseconds = 0,
    [ValidateRange(0, 10000)]
    [int]$MaxReports = 0,
    [ValidateRange(0, 10000)]
    [int]$MinimumRank = 0,
    [ValidateRange(0, 10000)]
    [int]$MaximumRank = 0,
    [switch]$FullCombatOnly,
    [switch]$HighOnly,
    [switch]$DetailedDiagnostics,
    [switch]$PreflightOnly
)

$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $false

$projectRoot = Split-Path -Parent $PSScriptRoot
$runnerPath = Join-Path $PSScriptRoot "run-unattended-test.ps1"
$reportsRootPath = [System.IO.Path]::GetFullPath($ReportsRoot)

if (-not (Test-Path -LiteralPath $runnerPath -PathType Leaf)) {
    throw "找不到无人值守测试入口：$runnerPath"
}
if (-not (Test-Path -LiteralPath $reportsRootPath -PathType Container)) {
    throw "找不到报告目录：$reportsRootPath"
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $OutputDirectory = Join-Path $projectRoot ".local/strategy-batch/results/$timestamp"
}
$outputPath = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null

$jsonlPath = Join-Path $outputPath "strategy-replay-batch.jsonl"
$jsonPath = Join-Path $outputPath "strategy-replay-batch.json"
$csvPath = Join-Path $outputPath "strategy-replay-batch.csv"
Set-Content -LiteralPath $jsonlPath -Value "" -NoNewline -Encoding UTF8

function Read-Manifest {
    if (-not [string]::IsNullOrWhiteSpace($ManifestPath)) {
        $resolvedManifestPath = [System.IO.Path]::GetFullPath($ManifestPath)
        if (-not (Test-Path -LiteralPath $resolvedManifestPath -PathType Leaf)) {
            throw "找不到清单：$resolvedManifestPath"
        }
        $parsed = Get-Content -LiteralPath $resolvedManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($null -ne $parsed.manifest) {
            return @($parsed.manifest)
        }
        return @($parsed)
    }

    if (-not [string]::IsNullOrWhiteSpace($WorkbookPath)) {
        $resolvedWorkbookPath = [System.IO.Path]::GetFullPath($WorkbookPath)
        $resolvedReaderPath = [System.IO.Path]::GetFullPath((Join-Path $projectRoot $WorkbookReaderScript))
        if (-not (Test-Path -LiteralPath $resolvedWorkbookPath -PathType Leaf)) {
            throw "找不到表格：$resolvedWorkbookPath"
        }
        if (-not (Test-Path -LiteralPath $resolvedReaderPath -PathType Leaf)) {
            throw "找不到表格清单读取器：$resolvedReaderPath"
        }
        $manifestText = & $NodePath $resolvedReaderPath $resolvedWorkbookPath 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0) {
            throw "读取表格失败：$manifestText"
        }
        $parsed = $manifestText | ConvertFrom-Json
        return @($parsed.manifest)
    }

    return @(
        Get-ChildItem -LiteralPath $reportsRootPath -Directory |
            ForEach-Object {
                [pscustomobject]@{
                    reportId = $_.Name
                    rank = $null
                    originalLoss = $null
                    manualLoss = $null
                    note = $null
                    encounter = $null
                }
            }
    )
}

function Get-NullableInt {
    param([object]$Value)

    if ($null -eq $Value -or [string]::IsNullOrWhiteSpace([string]$Value)) {
        return $null
    }
    return [int]$Value
}

function New-BatchResult {
    param(
        [object]$Entry,
        [string]$Status,
        [string]$Reason,
        [string]$ReplayStatePath,
        [Nullable[int]]$TurnNumber,
        [string]$Preset,
        [Nullable[int]]$SolverLoss,
        [Nullable[double]]$ElapsedSeconds,
        [string]$RunId,
        [object]$Policy
    )

    $manualLoss = Get-NullableInt $Entry.manualLoss
    $relativeToManual = $null
    if ($TurnNumber -eq 1 -and $null -ne $manualLoss -and $null -ne $SolverLoss) {
        $relativeToManual = $manualLoss - $SolverLoss
    }

    return [pscustomobject][ordered]@{
        reportId = [string]$Entry.reportId
        rank = Get-NullableInt $Entry.rank
        encounter = [string]$Entry.encounter
        originalLoss = Get-NullableInt $Entry.originalLoss
        manualLoss = $manualLoss
        note = [string]$Entry.note
        status = $Status
        reason = $Reason
        replayStatePath = $ReplayStatePath
        turnNumber = $TurnNumber
        comparisonScope = if ($null -ne $TurnNumber -and $TurnNumber -eq 1) { "full_combat" } elseif ($null -ne $TurnNumber) { "mid_combat" } else { $null }
        preset = $Preset
        bossHpStrategy = $BossHpStrategy
        policy = $Policy
        solverLoss = $SolverLoss
        relativeToManual = $relativeToManual
        meetsManual = if ($null -ne $relativeToManual) { $relativeToManual -ge 0 } else { $null }
        elapsedSeconds = $ElapsedSeconds
        runId = $RunId
        testedAt = (Get-Date).ToString("o")
    }
}

function Save-BatchResult {
    param([object]$Result)

    $script:results.Add($Result)
    $Result | ConvertTo-Json -Depth 20 -Compress | Add-Content -LiteralPath $jsonlPath -Encoding UTF8
}

function Find-ReplayCandidate {
    param([string]$ReportDirectory)

    $settingsPath = Join-Path $ReportDirectory "combat-solver/settings.json"
    if (-not (Test-Path -LiteralPath $settingsPath -PathType Leaf)) {
        return [pscustomobject]@{ candidate = $null; reason = "missing_reported_settings" }
    }
    $settings = Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($null -eq $settings.potionDirectives) {
        return [pscustomobject]@{ candidate = $null; reason = "missing_reported_potion_directives" }
    }
    $actBossStrategy = $BossHpStrategy
    $finalBossStrategy = $BossHpStrategy
    if ($BossHpStrategy -eq "Reported") {
        $actBossStrategy = [string]$settings.actTransitionBossHpStrategy
        $finalBossStrategy = [string]$settings.finalBossHpStrategy
        if ($actBossStrategy -notin @("ProgressionFirst", "MinimizeHpLoss") -or
            $finalBossStrategy -notin @("ProgressionFirst", "MinimizeHpLoss")) {
            return [pscustomobject]@{ candidate = $null; reason = "missing_or_invalid_reported_boss_policy" }
        }
    }
    $policy = [pscustomobject][ordered]@{
        potionPolicy = $PotionPolicy
        potionDirectives = @($settings.potionDirectives)
        actTransitionBossHpStrategy = $actBossStrategy
        finalBossHpStrategy = $finalBossStrategy
    }
    $forensicsRoot = Join-Path $ReportDirectory "combat-solver/forensics/current"
    $replayDirectory = Join-Path $forensicsRoot "replay-state"
    $runDirectory = Join-Path $forensicsRoot "run-state"
    if (-not (Test-Path -LiteralPath $replayDirectory -PathType Container)) {
        return [pscustomobject]@{ candidate = $null; reason = "missing_replay_state_directory" }
    }
    if (-not (Test-Path -LiteralPath $runDirectory -PathType Container)) {
        return [pscustomobject]@{ candidate = $null; reason = "missing_run_state_directory" }
    }

    $reasons = [System.Collections.Generic.List[string]]::new()
    $replayFiles = @(Get-ChildItem -LiteralPath $replayDirectory -File -Filter "*.json" |
        Where-Object {
            $_.Name -like "*AutoTurnStart.json" -or
            ($FullCombatOnly -and $_.Name -eq "000-combat_start.json")
        } | Sort-Object Name)
    if ($replayFiles.Count -eq 0) {
        return [pscustomobject]@{ candidate = $null; reason = "missing_combat_start_or_auto_turn_start" }
    }

    foreach ($replayFile in $replayFiles) {
        try {
            $state = Get-Content -LiteralPath $replayFile.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
        }
        catch {
            $reasons.Add("invalid_json")
            continue
        }

        $runPath = Join-Path $runDirectory ([System.IO.Path]::ChangeExtension($replayFile.Name, ".save"))
        if (-not (Test-Path -LiteralPath $runPath -PathType Leaf)) {
            $reasons.Add("missing_paired_run_state")
            continue
        }
        if ([int]$state.schemaVersion -ne 1) {
            $reasons.Add("unsupported_schema")
            continue
        }
        if ($null -eq $state.runRng -or [string]::IsNullOrWhiteSpace([string]$state.runRng.seed)) {
            $reasons.Add("missing_seed")
            continue
        }
        if ([string]::IsNullOrWhiteSpace([string]$state.encounterId) -or @($state.players).Count -eq 0) {
            $reasons.Add("missing_combat_identity")
            continue
        }
        if ($null -eq @($state.players)[0].orbs) {
            $reasons.Add("missing_orb_state")
            continue
        }
        if ([string]::IsNullOrWhiteSpace([string]$state.exactContinuationState) -or [string]$state.exactContinuationState -notmatch ";Y=") {
            $reasons.Add("missing_turn_card_history")
            continue
        }

        $turnNumber = if ($null -ne $state.roundNumber) {
            [int]$state.roundNumber
        }
        else {
            [int](@($state.players)[0].turnNumber)
        }
        if ($FullCombatOnly -and $turnNumber -ne 1) {
            $reasons.Add("mid_combat_excluded")
            continue
        }

        return [pscustomobject]@{
            candidate = [pscustomobject]@{
                replayPath = $replayFile.FullName
                runPath = $runPath
                state = $state
                turnNumber = $turnNumber
                policy = $policy
            }
            reason = $null
        }
    }

    $reason = if ($reasons.Count -gt 0) { ($reasons | Select-Object -Unique) -join "," } else { "no_replayable_auto_turn_start" }
    return [pscustomobject]@{ candidate = $null; reason = $reason }
}

function Get-ProjectedLoss {
    param([object]$TestResult)

    foreach ($check in @($TestResult.completedChecks)) {
        if ([string]$check -match "ProjectedBattleHpLost=(?<loss>\d+)") {
            return [int]$Matches.loss
        }
    }
    if ([string]$TestResult.error -match "首轮路线预计整场掉血\s+(?<loss>\d+)") {
        return [int]$Matches.loss
    }
    return $null
}

function Invoke-ReplayTest {
    param(
        [object]$Entry,
        [object]$Candidate,
        [ValidateSet("High", "VeryHigh")]
        [string]$Preset,
        [int]$TimeoutSeconds
    )

    $state = $Candidate.state
    $player = @($state.players)[0]
    $manualLoss = Get-NullableInt $Entry.manualLoss
    $expectedLoss = if ($null -ne $manualLoss) { $manualLoss } else { [int]::MaxValue }
    $scenarioId = "batch-$([string]$Entry.reportId)-$($Preset.ToLowerInvariant())-$([guid]::NewGuid().ToString('N').Substring(0, 8))"

    $monsterIds = @(
        @($state.creatures) |
            Where-Object { [string]$_.side -eq "Enemy" } |
            ForEach-Object { [string]$_.monsterId } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
            Select-Object -Unique
    )
    $runnerParameters = [ordered]@{
        ScenarioId = $scenarioId
        CharacterId = [string]$player.characterId
        Seed = [string]$state.runRng.seed
        RunSnapshotPath = $Candidate.runPath
        ReplayStatePath = $Candidate.replayPath
        EncounterId = [string]$state.encounterId
        Ascension = [int]$state.ascensionLevel
        ActIndexForTest = [int]$state.currentActIndex
        InitialPlayerHp = [int]$player.currentHp
        InitialPlayerMaxHp = [int]$player.maxHp
        InitialPlayerBlock = [int]$player.block
        InitialPlayerEnergy = [int]$player.energy
        InitialPlayerStars = [int]$player.stars
        PerformancePresetForTest = $Preset
        PotionPolicyForTest = $Candidate.policy.potionPolicy
        PotionDirectivesForTestJson = ConvertTo-Json -InputObject $Candidate.policy.potionDirectives -Depth 5 -Compress
        ActTransitionBossHpStrategyForTest = $Candidate.policy.actTransitionBossHpStrategy
        FinalBossHpStrategyForTest = $Candidate.policy.finalBossHpStrategy
        SearchMaxDegreeOfParallelismForTest = $SearchParallelism
        ExpectedInitialProjectedBattleHpLostAtMost = $expectedLoss
        ExpectedInitialOnlyDeathRoutesFound = 0
        StopAfterInitialSolverResultAssertion = $true
        TimeoutSeconds = $TimeoutSeconds
        ExitOnComplete = $true
    }
    if ($DetailedDiagnostics) {
        $runnerParameters.Add("EnableDetailedDiagnosticLogsForTest", 1)
    }
    if ($ShortBudgetOverrideMilliseconds -gt 0) {
        $runnerParameters.Add("ShortSearchBudgetOverrideMilliseconds", $ShortBudgetOverrideMilliseconds)
    }
    if ($DeepBudgetOverrideMilliseconds -gt 0) {
        $runnerParameters.Add("DeepSearchBudgetOverrideMilliseconds", $DeepBudgetOverrideMilliseconds)
    }
    if ($monsterIds.Count -gt 0) {
        $runnerParameters.Add("AdditionalMonsterId", [string[]]$monsterIds)
    }

    $startedAt = Get-Date
    $payloadEnvironmentVariable = "COMBATSOLVER_BATCH_RUNNER_PAYLOAD"
    $previousPayload = [Environment]::GetEnvironmentVariable($payloadEnvironmentVariable, "Process")
    $payload = @{
        runnerPath = $runnerPath
        parameters = $runnerParameters
    } | ConvertTo-Json -Depth 20 -Compress
    try {
        [Environment]::SetEnvironmentVariable($payloadEnvironmentVariable, $payload, "Process")
        $childCommand = '$payload = $env:COMBATSOLVER_BATCH_RUNNER_PAYLOAD | ConvertFrom-Json -AsHashtable; $runnerPath = $payload.runnerPath; $parameters = $payload.parameters; & $runnerPath @parameters'
        $launcherOutput = & pwsh -NoProfile -Command $childCommand 2>&1 | Out-String
        $exitCode = $LASTEXITCODE
    }
    finally {
        [Environment]::SetEnvironmentVariable($payloadEnvironmentVariable, $previousPayload, "Process")
    }
    $elapsed = ((Get-Date) - $startedAt).TotalSeconds

    $runtimePath = Join-Path ([Environment]::GetFolderPath("LocalApplicationData")) "CombatSolver/headless-runtime"
    $resultPath = Join-Path $runtimePath "Roaming/SlayTheSpire2/combat_solver_test_result.json"
    $testResult = $null
    if (Test-Path -LiteralPath $resultPath -PathType Leaf) {
        try {
            $parsedResult = Get-Content -LiteralPath $resultPath -Raw -Encoding UTF8 | ConvertFrom-Json
            if ([string]$parsedResult.scenarioId -eq $scenarioId) {
                $testResult = $parsedResult
            }
        }
        catch {
            $testResult = $null
        }
    }

    if ($null -eq $testResult) {
        $tail = ($launcherOutput -split "`r?`n" | Where-Object { $_ } | Select-Object -Last 3) -join " | "
        return [pscustomobject]@{
            status = "failed_runtime"
            reason = "missing_result exit=$exitCode $tail".Trim()
            loss = $null
            elapsed = [math]::Round($elapsed, 2)
            runId = $null
        }
    }

    $evidencePrefix = Join-Path $outputPath ([string]$testResult.runId)
    Copy-Item -LiteralPath $resultPath -Destination "$evidencePrefix-result.json"
    Copy-Item -LiteralPath (Join-Path $runtimePath "godot-headless.log") -Destination "$evidencePrefix-godot.log"
    $metrics = $testResult.solverMetrics
    $completeVictory = $null -ne $metrics -and
        $null -ne $metrics.combatEndedTurn -and
        $metrics.finalEnemyHp -eq 0 -and
        $metrics.finalHp -gt 0 -and
        -not $metrics.onlyDeathRoutes
    $loss = if ($completeVictory) { Get-ProjectedLoss $testResult } else { $null }
    $errorText = [string]$testResult.error
    $timedOut = $errorText -match "超时|timeout|timed out" -or [string]$testResult.status -eq "TimedOut"
    $status = if ($timedOut) {
        "timeout"
    }
    elseif (-not $completeVictory -and ($null -ne $metrics -or [string]$testResult.status -eq "Passed")) {
        "unconfirmed_victory"
    }
    elseif ($null -ne $loss -and $null -ne $manualLoss -and $loss -gt $manualLoss) {
        "quality_gap"
    }
    elseif ([string]$testResult.status -eq "Passed") {
        "passed"
    }
    elseif ($null -ne $loss -and $null -eq $manualLoss) {
        "observed"
    }
    else {
        "failed_runtime"
    }

    return [pscustomobject]@{
        status = $status
        reason = if ($status -eq "passed" -or $status -eq "observed" -or $status -eq "quality_gap") { $null } else { $errorText }
        loss = $loss
        elapsed = [math]::Round($elapsed, 2)
        runId = [string]$testResult.runId
    }
}

$entries = @(Read-Manifest)
if ($ReportId.Count -gt 0) {
    $requestedIds = [System.Collections.Generic.HashSet[string]]::new([string[]]$ReportId)
    $entries = @($entries | Where-Object { $requestedIds.Contains([string]$_.reportId) })
}
$entries = @($entries | Sort-Object @{ Expression = { if ($null -eq $_.rank) { [int]::MaxValue } else { [int]$_.rank } } }, reportId)
if ($MinimumRank -gt 0) {
    $entries = @($entries | Where-Object { $null -ne $_.rank -and [int]$_.rank -ge $MinimumRank })
}
if ($MaximumRank -gt 0) {
    $entries = @($entries | Where-Object { $null -ne $_.rank -and [int]$_.rank -le $MaximumRank })
}
if ($MaxReports -gt 0) {
    $entries = @($entries | Select-Object -First $MaxReports)
}

$results = [System.Collections.Generic.List[object]]::new()
$index = 0
foreach ($entry in $entries) {
    $index++
    $entryReportId = [string]$entry.reportId
    Write-Host "[$index/$($entries.Count)] $entryReportId" -ForegroundColor Cyan

    if ($ExcludeReportId -contains $entryReportId) {
        Save-BatchResult (New-BatchResult -Entry $entry -Status "excluded" -Reason "explicitly_excluded" -ReplayStatePath $null -TurnNumber $null -Preset $null -SolverLoss $null -ElapsedSeconds $null -RunId $null)
        Write-Host "  跳过：已排除"
        continue
    }

    $reportDirectory = Join-Path $reportsRootPath $entryReportId
    if (-not (Test-Path -LiteralPath $reportDirectory -PathType Container)) {
        Save-BatchResult (New-BatchResult -Entry $entry -Status "blocked_preflight" -Reason "missing_report_directory" -ReplayStatePath $null -TurnNumber $null -Preset $null -SolverLoss $null -ElapsedSeconds $null -RunId $null)
        Write-Host "  跳过：报告目录不存在"
        continue
    }

    $preflight = Find-ReplayCandidate $reportDirectory
    if ($null -eq $preflight.candidate) {
        Save-BatchResult (New-BatchResult -Entry $entry -Status "blocked_preflight" -Reason $preflight.reason -ReplayStatePath $null -TurnNumber $null -Preset $null -SolverLoss $null -ElapsedSeconds $null -RunId $null)
        Write-Host "  跳过：$($preflight.reason)"
        continue
    }

    $candidate = $preflight.candidate
    if ($PreflightOnly) {
        Save-BatchResult (New-BatchResult -Entry $entry -Status "ready" -Reason $null -ReplayStatePath $candidate.replayPath -TurnNumber $candidate.turnNumber -Preset $null -SolverLoss $null -ElapsedSeconds $null -RunId $null -Policy $candidate.policy)
        Write-Host "  可回放：Turn $($candidate.turnNumber)"
        continue
    }

    $preset = if ($HighOnly) { "High" } else { "VeryHigh" }
    $timeoutSeconds = if ($HighOnly) { $HighTimeoutSeconds } else { $VeryHighTimeoutSeconds }
    Write-Host "  $preset / DOP $SearchParallelism" -ForegroundColor DarkCyan
    $replay = Invoke-ReplayTest -Entry $entry -Candidate $candidate -Preset $preset -TimeoutSeconds $timeoutSeconds
    $presetStatus = if ($HighOnly) { "high" } else { "very_high" }
    if ($replay.status -in @("passed", "observed")) {
        $finalStatus = "$($replay.status)_$presetStatus"
        Save-BatchResult (New-BatchResult -Entry $entry -Status $finalStatus -Reason $replay.reason -ReplayStatePath $candidate.replayPath -TurnNumber $candidate.turnNumber -Preset $preset -SolverLoss $replay.loss -ElapsedSeconds $replay.elapsed -RunId $replay.runId -Policy $candidate.policy)
        Write-Host "  完成：$preset 战损 $($replay.loss)" -ForegroundColor Green
        continue
    }
    if ($replay.status -eq "quality_gap") {
        Save-BatchResult (New-BatchResult -Entry $entry -Status "quality_gap_$presetStatus" -Reason "$preset=$($replay.loss)" -ReplayStatePath $candidate.replayPath -TurnNumber $candidate.turnNumber -Preset $preset -SolverLoss $replay.loss -ElapsedSeconds $replay.elapsed -RunId $replay.runId -Policy $candidate.policy)
        Write-Host "  $preset 策略缺口：战损 $($replay.loss)" -ForegroundColor Red
        continue
    }

    Save-BatchResult (New-BatchResult -Entry $entry -Status $replay.status -Reason $replay.reason -ReplayStatePath $candidate.replayPath -TurnNumber $candidate.turnNumber -Preset $preset -SolverLoss $replay.loss -ElapsedSeconds $replay.elapsed -RunId $replay.runId -Policy $candidate.policy)
    Write-Host "  结束：$($replay.status)" -ForegroundColor Yellow
}

ConvertTo-Json -InputObject @($results) -Depth 20 | Set-Content -LiteralPath $jsonPath -Encoding UTF8
$results | Export-Csv -LiteralPath $csvPath -NoTypeInformation -Encoding UTF8

$summary = $results | Group-Object status | Sort-Object Name | ForEach-Object { "$($_.Name)=$($_.Count)" }
Write-Host "批量完成：$($summary -join ', ')" -ForegroundColor Cyan
Write-Host "JSON：$jsonPath"
Write-Host "CSV：$csvPath"
