#requires -Version 7.4
# One fresh, owned Godot process. Performance samples never enable strict replay.
param(
    [Parameter(Mandatory)][string]$GameRoot,
    [Parameter(Mandatory)][string]$RitsuWorkshopRoot,
    [Parameter(Mandatory)][string]$Build,
    [Parameter(Mandatory)][string]$Scenario,
    [Parameter(Mandatory)][string]$Output,
    [ValidateSet('default','server-generational')][string]$RuntimeProfile='default',
    [ValidateRange(1,16)][int]$Dop=8,
    [ValidateRange(1,1048576)][int]$MemoryReservationMiB=12288,
    [ValidateRange(1,3600)][int]$TimeoutSeconds=360
)
$ErrorActionPreference='Stop'
$repository=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$launcher=Join-Path $repository 'tools/run-unattended-test.ps1'
$instance='gc-bench-'+[Guid]::NewGuid().ToString('N').Substring(0,12)
$runtime=Join-Path $repository ".local/headless-instances/$instance"
if($env:COMBATSOLVER_HEADLESS_ROOT){ throw 'Unset COMBATSOLVER_HEADLESS_ROOT for a fresh benchmark instance.' }
$Output=[IO.Path]::GetFullPath($Output)
if(Test-Path -LiteralPath $Output){ throw "Output already exists: $Output" }
New-Item -ItemType Directory -Path $Output | Out-Null
$pwsh=(Get-Process -Id $PID).Path
function Invoke-OwnedStop([switch]$Cleanup) {
    $stopArguments=@('-NoProfile','-File',$launcher,'-Sts2GameRoot',$GameRoot,'-RitsuWorkshopRoot',$RitsuWorkshopRoot,'-HeadlessInstance',$instance,'-StopInstance')
    if($Cleanup){ $stopArguments+='-CleanupInstanceOnExit' }
    & $pwsh @stopArguments | Out-Null
    if($LASTEXITCODE -ne 0){ throw 'Owned instance cleanup failed.' }
}
$child=$null; $game=$null
try {
    Invoke-OwnedStop
    $data=Join-Path $runtime 'Roaming/SlayTheSpire2'
    New-Item -ItemType Directory -Path $data -Force | Out-Null
    $settingsPath=Join-Path $data 'combat_solver_settings.json'
    $settings=@{ performanceMigrationVersion=246; performancePreset='VeryHigh'; enableNoGcRegion=$true;
        noGcRegionBudgetGigabytes=16; searchTimeLimitSeconds=300; searchMaxDegreeOfParallelism=$Dop;
        enableDetailedDiagnosticLogs=$false; onlineStatisticsEnabled=$false }
    $settings | ConvertTo-Json | Set-Content -LiteralPath $settingsPath -Encoding utf8NoBOM
    $settingsBefore=(Get-FileHash -LiteralPath $settingsPath -Algorithm SHA256).Hash
    $definition=Get-Content -LiteralPath $Scenario -Raw | ConvertFrom-Json
    if($definition.mode -ne 'Search' -or $definition.fixedSearchBudget -ne $false){
        throw 'Scenario must explicitly use Search mode and fixedSearchBudget=false.'
    }
    Copy-Item -LiteralPath $Scenario -Destination (Join-Path $Output 'input.json')
    $start=[Diagnostics.ProcessStartInfo]::new($pwsh)
    $start.UseShellExecute=$false; $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    $arguments=@('-NoProfile','-File',$launcher,'-Sts2GameRoot',$GameRoot,'-RitsuWorkshopRoot',$RitsuWorkshopRoot,
        '-CombatSolverBuildDir',[IO.Path]::GetFullPath($Build),'-HeadlessInstance',$instance,
        '-RuntimeProfile',$RuntimeProfile,'-GeneratedScenarioPath',(Join-Path $Output 'input.json'),
        '-EvidenceDirectory',$Output,'-ScenarioId','RUNTIME-GC-PERFORMANCE','-PerformancePresetForTest','VeryHigh',
        '-SearchBudgetOverrideMilliseconds','300000','-SearchMaxExpandedNodesForTest','500000',
        '-SearchMaxDegreeOfParallelismForTest',[string]$Dop,'-EnableDetailedDiagnosticLogsForTest','0',
        '-HeadlessCpuReservation',[string]$Dop,'-HeadlessMemoryReservationMiB',[string]$MemoryReservationMiB,
        '-TimeoutSeconds',[string]$TimeoutSeconds,'-ExitOnComplete')
    foreach($argument in $arguments){ $start.ArgumentList.Add($argument) }
    $arguments | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Output 'command.json')
    $child=[Diagnostics.Process]::Start($start)
    $stdout=$child.StandardOutput.ReadToEndAsync(); $stderr=$child.StandardError.ReadToEndAsync()
    $samples=[Collections.Generic.List[object]]::new()
    $processPeak=0L; $processCpu=0d
    $markerPath=Join-Path $runtime 'process.json'
    while(-not $child.WaitForExit(100)){
        if($null -eq $game -and (Test-Path -LiteralPath $markerPath)){
            $marker=Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
            $game=Get-Process -Id $marker.pid -ErrorAction Stop
            $gameHandle=$game.SafeHandle # Preserve this exact process identity through exit.
        }
        if($null -ne $game -and -not $game.HasExited){
            $game.Refresh()
            $processPeak=[Math]::Max($processPeak,$game.PeakWorkingSet64)
            $processCpu=$game.TotalProcessorTime.TotalMilliseconds
            $samples.Add(@{ utc=[DateTimeOffset]::UtcNow.ToString('O'); workingSetBytes=$game.WorkingSet64;
                privateBytes=$game.PrivateMemorySize64; cpuMilliseconds=$game.TotalProcessorTime.TotalMilliseconds })
        }
    }
    $stdout.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $Output 'launcher.log')
    $stderr.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $Output 'launcher.stderr.log')
    $settingsAfter=(Get-FileHash -LiteralPath $settingsPath -Algorithm SHA256).Hash
    $measure=@{ launcherExitCode=$child.ExitCode; settingsBytesUnchanged=$settingsBefore -eq $settingsAfter;
        dllSha256=(Get-FileHash -LiteralPath (Join-Path $Build 'CombatSolver.dll') -Algorithm SHA256).Hash;
        runtimeProfile=$RuntimeProfile; dop=$Dop; samples=$samples.ToArray(); intervalMilliseconds=100 }
    if($null -ne $game){
        $measure.processPeakWorkingSetBytes=$processPeak
        $measure.processCpuMilliseconds=$processCpu
        $measure.gamePid=$game.Id
        $measure.gameExitCode=if($game.HasExited){$game.ExitCode}else{$null}
    }
    $measure | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Output 'measurement.json')
    foreach($log in @(Get-ChildItem -LiteralPath $runtime -Filter '*.log' -File)){
        Copy-Item -LiteralPath $log.FullName -Destination $Output
    }
    $journal=Join-Path $data 'logs/CombatSolver'
    if(Test-Path -LiteralPath $journal){
        Copy-Item -LiteralPath $journal -Destination (Join-Path $Output 'diagnostic-logs') -Recurse
    }
    if($child.ExitCode -ne 0){ throw "Native launcher failed; see $Output/launcher.stderr.log" }
    $result=Get-Content -LiteralPath (Join-Path $Output 'result.json') -Raw | ConvertFrom-Json
    if($child.ExitCode -ne 0 -or $result.status -ne 'Passed'){ throw "Native run failed: $($result.status); $($result.error)" }
    $effective=(Get-Content -LiteralPath (Join-Path $Output 'search-result.json') -Raw | ConvertFrom-Json).runtime
    $server=$RuntimeProfile -eq 'server-generational'
    $expectedStatus=if($server){'Active'}else{'Default'}
    if($effective.serverGc -ne $server -or $effective.profile -ne $expectedStatus -or
        $effective.savedNoGcRegionEnabled -ne $true -or $effective.effectiveNoGcRegionEnabled -ne (-not $server)){
        throw 'Actual runtime configuration does not match the requested A/B mode.'
    }
    if(-not $measure.settingsBytesUnchanged){ throw 'Benchmark unexpectedly rewrote saved settings.' }
    @{ output=$Output; status=$result.status; milliseconds=$result.solverMetrics.totalElapsedMilliseconds;
        processPeakWorkingSetBytes=$measure.processPeakWorkingSetBytes; settingsBytesUnchanged=$true } | ConvertTo-Json -Compress
} finally {
    if($null -ne $child){
        if(-not $child.HasExited){ $child.Kill(); $child.WaitForExit() }
        $child.Dispose()
    }
    Invoke-OwnedStop -Cleanup
    if($null -ne $game){ $game.Dispose() }
}
