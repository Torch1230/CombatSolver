#requires -Version 7.4

[CmdletBinding()]
param(
    [ValidateRange(1, 10000)]
    [int]$StartAt = 1,
    [ValidateRange(0, 10000)]
    [int]$MaxCases = 0,
    [switch]$ContinueOnFailure,
    [string]$Sts2GameRoot = "C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2",
    [string]$RitsuWorkshopRoot = "C:\Program Files (x86)\Steam\steamapps\workshop\content\2868840\3747602295",
    [string]$HeadlessInstance = "",
    [ValidateSet('exclusive', 'parallel')]
    [string]$HeadlessExecutionMode = 'exclusive',
    [ValidateRange(1, 1048576)]
    [int]$HeadlessMemoryReservationMiB = 4096,
    [ValidateRange(1, 1024)]
    [int]$HeadlessCpuReservation = 2,
    [ValidateRange(1, 3600)]
    [int]$HeadlessQueueTimeoutSeconds = 120,
    [string]$CombatSolverBuildDir = "",
    [string]$ResultsPath = ".local\headless-matrix-results.jsonl"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($null -eq ("CombatSolverHeadlessMatrixCancellation" -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Diagnostics;
using System.Threading;

public static class CombatSolverHeadlessMatrixCancellation
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

    public static bool WaitForExit(Process process, bool observeCancellation)
    {
        while (!process.WaitForExit(100))
        {
            if (observeCancellation && IsCancellationRequested)
                return false;
        }
        return !(observeCancellation && IsCancellationRequested);
    }

    private static void HandleCancelKeyPress(object sender, ConsoleCancelEventArgs args)
    {
        args.Cancel = true;
        Interlocked.Exchange(ref requested, 1);
    }
}
"@
}

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $repoRoot
$runner = Join-Path $PSScriptRoot "run-unattended-test.ps1"
. (Join-Path $PSScriptRoot 'headless-runtime.ps1')

$commands = @(Get-Content -LiteralPath "docs\TEST_MATRIX.md" |
    Where-Object { $_ -match '^pwsh -NoProfile -File tools\\run-unattended-test\.ps1(?: |$)' })
if ($commands.Count -eq 0) {
    throw "No PowerShell unattended commands were found in docs\TEST_MATRIX.md."
}
if ($StartAt -gt $commands.Count) {
    throw "StartAt exceeds the $($commands.Count) documented commands."
}

function Get-MissingFixtureReason([string]$ScenarioId) {
    if ($ScenarioId -eq "CHOICES-PARADOX-SCROLLS-0160") {
        if ([string]::IsNullOrWhiteSpace($env:CHOICES_PARADOX_RUN_SNAPSHOT_PATH) -or
            [string]::IsNullOrWhiteSpace($env:CHOICES_PARADOX_PROGRESS_SNAPSHOT_PATH) -or
            -not (Test-Path -LiteralPath $env:CHOICES_PARADOX_RUN_SNAPSHOT_PATH -PathType Leaf) -or
            -not (Test-Path -LiteralPath $env:CHOICES_PARADOX_PROGRESS_SNAPSHOT_PATH -PathType Leaf)) {
            return "missing external choices-paradox run/progress snapshots"
        }
    }
    if ($ScenarioId -in @("QUEEN-CHAINS-REUSE-FINAL-085", "CORPSE-SLUGS-USER-RUN-073")) {
        if ([string]::IsNullOrWhiteSpace($env:RUN_SNAPSHOT_PATH) -or
            -not (Test-Path -LiteralPath $env:RUN_SNAPSHOT_PATH -PathType Leaf)) {
            return "missing external user profile save"
        }
    }
    return $null
}

$resultsFullPath = [IO.Path]::GetFullPath($ResultsPath)
$resultsDirectory = Split-Path -Parent $resultsFullPath
New-Item -ItemType Directory -Path $resultsDirectory -Force | Out-Null
if (Test-Path -LiteralPath $resultsFullPath -PathType Leaf) {
    Remove-Item -LiteralPath $resultsFullPath -Force
}

$escapedGameRoot = $Sts2GameRoot.Replace("'", "''")
$escapedRitsuRoot = $RitsuWorkshopRoot.Replace("'", "''")
$attempted = 0
$passed = 0
$failed = 0
$skipped = 0
$ranCase = $false
$suiteStopwatch = [Diagnostics.Stopwatch]::StartNew()
if ([Environment]::ProcessorCount -eq 1 -and -not $PSBoundParameters.ContainsKey('HeadlessCpuReservation')) {
    $HeadlessCpuReservation = 1
}
$runtimeContext = New-HeadlessRuntimeContext $repoRoot $Sts2GameRoot $HeadlessInstance `
    $HeadlessExecutionMode $HeadlessMemoryReservationMiB $HeadlessCpuReservation $HeadlessQueueTimeoutSeconds
$headlessRoot = $runtimeContext.Root
New-Item -ItemType Directory -Path $headlessRoot -Force | Out-Null
$launcherLock = [IO.File]::Open((Join-Path $headlessRoot 'launcher.lock'),
    [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try {
    Initialize-HeadlessRuntimeOwner $runtimeContext
    $matrixLockPath = Join-Path $headlessRoot 'matrix.lock'
    Assert-HeadlessNoReparsePoint $matrixLockPath
    $matrixLock = [IO.File]::Open($matrixLockPath,
        [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
} finally {
    $launcherLock.Dispose()
}
$processMarkerPath = Join-Path $headlessRoot "process.json"
$runtimeArguments = @('-HeadlessInstance', $runtimeContext.Instance,
    '-HeadlessExecutionMode', $HeadlessExecutionMode,
    '-HeadlessMemoryReservationMiB', [string]$HeadlessMemoryReservationMiB,
    '-HeadlessCpuReservation', [string]$HeadlessCpuReservation,
    '-HeadlessQueueTimeoutSeconds', [string]$HeadlessQueueTimeoutSeconds)
if (-not [string]::IsNullOrWhiteSpace($CombatSolverBuildDir)) {
    $runtimeArguments += @('-CombatSolverBuildDir', [IO.Path]::GetFullPath($CombatSolverBuildDir))
}
# Parameter names must be tokens, not quoted positional values in a command.
$runtimeCommandSuffix = ''
for ($argumentIndex = 0; $argumentIndex -lt $runtimeArguments.Count; $argumentIndex += 2) {
    $runtimeCommandSuffix += ' ' + $runtimeArguments[$argumentIndex] + " '" +
        $runtimeArguments[$argumentIndex + 1].Replace("'", "''") + "'"
}
$primaryError = $null
$cleanupError = $null
$cleanupExitCode = 0
$lifecycleMode = "documented"
$pwshExecutable = (Get-Command pwsh -ErrorAction Stop).Source

function Start-MatrixPwshProcess([string[]]$Arguments) {
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $script:pwshExecutable
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.WorkingDirectory = $script:repoRoot
    $startInfo.Environment['COMBATSOLVER_HEADLESS_ROOT'] = $script:headlessRoot
    foreach ($argument in $Arguments) {
        $startInfo.ArgumentList.Add($argument)
    }
    $child = [Diagnostics.Process]::new()
    $child.StartInfo = $startInfo
    if (-not $child.Start()) {
        $child.Dispose()
        throw "Could not start the matrix PowerShell child process."
    }
    return $child
}

function Invoke-MatrixCaseCommand([string]$CaseCommand) {
    if ([CombatSolverHeadlessMatrixCancellation]::IsCancellationRequested) {
        throw [OperationCanceledException]::new("Headless matrix cancellation was requested.")
    }
    $wrapper = @"
`$ErrorActionPreference = 'Stop'
try {
    $CaseCommand
    `$nativeExitCode = if (`$null -eq `$LASTEXITCODE) { 0 } else { [int]`$LASTEXITCODE }
    exit `$nativeExitCode
} catch {
    [Console]::Error.WriteLine(`$_.Exception.ToString())
    exit 1
}
"@
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($wrapper))
    $child = Start-MatrixPwshProcess @("-NoProfile", "-EncodedCommand", $encoded)
    try {
        $completed = [CombatSolverHeadlessMatrixCancellation]::WaitForExit($child, $true)
        if (-not $completed) {
            if (-not $child.HasExited) {
                $child.Kill($true)
            }
            [CombatSolverHeadlessMatrixCancellation]::WaitForExit($child, $false) | Out-Null
            throw [OperationCanceledException]::new("Headless matrix cancellation was requested.")
        }
        return [int]$child.ExitCode
    } finally {
        $child.Dispose()
    }
}

function Invoke-HeadlessCleanup([switch]$AllowBeforeFirstCase) {
    if (-not $script:ranCase -and -not $AllowBeforeFirstCase.IsPresent) {
        return 0
    }
    if (-not (Test-Path -LiteralPath $script:processMarkerPath -PathType Leaf)) {
        if (Test-HeadlessUnboundGame $script:headlessRoot) {
            throw 'Markerless instance process preserved: cleanup cannot prove ownership.'
        }
        return 0
    }

    if (-not (Test-Path -LiteralPath (Join-Path $script:headlessRoot 'instance.json') -PathType Leaf)) {
        throw 'Runtime owner is missing; cleanup preserved the process.'
    }
    Initialize-HeadlessRuntimeOwner $script:runtimeContext
    $marker = Get-Content -LiteralPath $script:processMarkerPath -Raw | ConvertFrom-Json -AsHashtable
    if ($marker.pid -isnot [long] -and $marker.pid -isnot [int]) {
        throw 'Invalid process marker preserved; cleanup requires a numeric PID.'
    }
    if ($marker.pid -le 0 -or [string]::IsNullOrWhiteSpace([string]$marker.processStartTimeUtc) -or
        $marker.instance -ne $script:runtimeContext.Instance -or
        -not [string]::Equals($marker.runtimeRoot, $script:headlessRoot, [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals($marker.executable, (Join-Path $script:headlessRoot 'game\SlayTheSpire2.exe'), [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals($marker.appData, (Join-Path $script:headlessRoot 'Roaming'), [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals($marker.dataDir, (Join-Path $script:headlessRoot 'Roaming\SlayTheSpire2'), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Invalid or foreign process marker preserved; cleanup refused.'
    }
    $markerProcessLabel = [string]$marker.pid
    Write-Host "MATRIX_CLEANUP_BEGIN pid=$markerProcessLabel"
    $cleanupArguments = @(
        "-NoProfile",
        "-File", $script:runner,
        "-StopInstance",
        "-Sts2GameRoot", $script:Sts2GameRoot,
        "-RitsuWorkshopRoot", $script:RitsuWorkshopRoot) + $script:runtimeArguments
    $child = Start-MatrixPwshProcess $cleanupArguments
    try {
        [CombatSolverHeadlessMatrixCancellation]::WaitForExit($child, $false) | Out-Null
        $exitCode = [int]$child.ExitCode
    } finally {
        $child.Dispose()
    }
    Write-Host "MATRIX_CLEANUP_END exit_code=$exitCode"
    return [int]$exitCode
}

[CombatSolverHeadlessMatrixCancellation]::Install()
$matrixCancellationInstalled = $true
Write-Host (
    "MATRIX_BEGIN total=$($commands.Count) start_at=$StartAt max_cases=$MaxCases " +
    "lifecycle_mode=$lifecycleMode")

try {
    if ([CombatSolverHeadlessMatrixCancellation]::IsCancellationRequested) {
        throw [OperationCanceledException]::new("Headless matrix cancellation was requested.")
    }
    $preflightCleanupExitCode = Invoke-HeadlessCleanup -AllowBeforeFirstCase
    if ($preflightCleanupExitCode -ne 0) {
        throw "Matrix preflight cleanup request exited with code $preflightCleanupExitCode."
    }
    for ($offset = $StartAt - 1; $offset -lt $commands.Count; $offset++) {
        $index = $offset + 1
        $command = $commands[$offset]
        if ($command -notmatch '-ScenarioId\s+([^\s]+)') {
            throw "Command $index does not contain a ScenarioId: $command"
        }
        $scenarioId = $Matches[1].Trim("'", '"')
        $fixtureReason = Get-MissingFixtureReason $scenarioId
        if ($null -ne $fixtureReason) {
            $skipped++
            [ordered]@{
                index = $index
                scenarioId = $scenarioId
                status = "SkippedMissingFixture"
                reason = $fixtureReason
                elapsedMilliseconds = 0
                exitCode = $null
            } | ConvertTo-Json -Compress | Add-Content -LiteralPath $resultsFullPath -Encoding UTF8
            Write-Host "MATRIX_SKIP index=$index scenario=$scenarioId reason=$fixtureReason"
            if ($command -match '(?i)(?:^|\s)-ExitOnComplete(?:\s|$)') {
                $skippedBoundaryCleanupExitCode = Invoke-HeadlessCleanup -AllowBeforeFirstCase
                if ($skippedBoundaryCleanupExitCode -ne 0) {
                    throw (
                        "Skipped exit boundary cleanup request exited with code " +
                        "$skippedBoundaryCleanupExitCode.")
                }
            }
            continue
        }
        if ($MaxCases -gt 0 -and $attempted -ge $MaxCases) {
            break
        }

        $attempted++
        $ranCase = $true
        $caseCommand = $command
        $caseCommand += " -Sts2GameRoot '$escapedGameRoot' -RitsuWorkshopRoot '$escapedRitsuRoot'"
        $caseCommand += $runtimeCommandSuffix

        $caseStopwatch = [Diagnostics.Stopwatch]::StartNew()
        Write-Host "MATRIX_CASE_BEGIN index=$index scenario=$scenarioId"
        $exitCode = 1
        $failure = $null
        try {
            $exitCode = Invoke-MatrixCaseCommand $caseCommand
        } catch [OperationCanceledException] {
            throw
        } catch {
            $failure = $_.Exception.Message
            $exitCode = 1
            Write-Error -ErrorAction Continue (
                "MATRIX_CASE_EXCEPTION index=$index scenario=$scenarioId error=$failure")
        }
        $caseStopwatch.Stop()

        $status = if ($exitCode -eq 0) { "Passed" } else { "Failed" }
        if ($exitCode -eq 0) {
            $passed++
        } else {
            $failed++
        }
        $elapsedMilliseconds = [Math]::Round($caseStopwatch.Elapsed.TotalMilliseconds, 3)
        [ordered]@{
            index = $index
            scenarioId = $scenarioId
            status = $status
            elapsedMilliseconds = $elapsedMilliseconds
            exitCode = $exitCode
            error = $failure
            command = $caseCommand
            documentedCommand = $command
            lifecycleMode = $lifecycleMode
        } | ConvertTo-Json -Compress | Add-Content -LiteralPath $resultsFullPath -Encoding UTF8
        Write-Host (
            "MATRIX_CASE_END index=$index scenario=$scenarioId status=$status " +
            "elapsed_ms=$elapsedMilliseconds exit_code=$exitCode")

        if ($exitCode -ne 0) {
            try {
                $failureCleanupExitCode = Invoke-HeadlessCleanup
                if ($failureCleanupExitCode -ne 0) {
                    throw "Matrix cleanup request exited with code $failureCleanupExitCode."
                }
            } catch {
                $primaryError = $_
                break
            }
            if (-not $ContinueOnFailure.IsPresent) {
                break
            }
        }
    }
} catch {
    $primaryError = $_
} finally {
    try {
        $cleanupExitCode = Invoke-HeadlessCleanup
        if ($cleanupExitCode -ne 0) {
            throw "Matrix cleanup request exited with code $cleanupExitCode."
        }
    } catch {
        $cleanupError = $_
        if ($cleanupExitCode -eq 0) {
            $cleanupExitCode = 1
        }
    }
    $suiteStopwatch.Stop()
    Write-Host (
        "MATRIX_END total=$($commands.Count) attempted=$attempted passed=$passed failed=$failed " +
        "skipped=$skipped cleanup_exit_code=$cleanupExitCode " +
        "elapsed_ms=$([Math]::Round($suiteStopwatch.Elapsed.TotalMilliseconds, 3)) " +
        "results=$resultsFullPath")
    if ($matrixCancellationInstalled) {
        [CombatSolverHeadlessMatrixCancellation]::Uninstall()
    }
    $matrixLock.Dispose()
}

if ([CombatSolverHeadlessMatrixCancellation]::IsCancellationRequested -or
    ($null -ne $primaryError -and $primaryError.Exception -is [OperationCanceledException])) {
    if ($null -ne $primaryError) {
        Write-Error -ErrorAction Continue $primaryError
    }
    if ($null -ne $cleanupError) {
        Write-Error -ErrorAction Continue $cleanupError
    }
    exit 130
}
if ($null -ne $primaryError -and $null -ne $cleanupError) {
    throw "Matrix failed: $($primaryError.Exception.Message) Cleanup also failed: $($cleanupError.Exception.Message)"
}
if ($null -ne $primaryError) {
    throw $primaryError
}
if ($null -ne $cleanupError) {
    throw $cleanupError
}
if ($failed -gt 0) {
    exit 1
}
