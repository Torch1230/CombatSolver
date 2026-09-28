param(
    [ValidateRange(1, 65535)]
    [int]$Port = 33771
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$session = Join-Path $repositoryRoot ('.local/multiplayer-p0/enet-2-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $session -Force | Out-Null
$processes = @()

foreach ($seat in 0..1) {
    $peer = Join-Path $session "peer-$seat"
    New-Item -ItemType Directory -Path $peer -Force | Out-Null
    $input = Join-Path $session "input-$seat.json"
    @{
        mode = if ($seat -eq 0) { 'host' } else { 'client' }
        playerCount = 2
        seat = $seat
        port = $Port
        coordinationDirectory = $session
        expectedGameVersion = '0.111.0'
    } | ConvertTo-Json | Set-Content -LiteralPath $input -Encoding utf8
    $instance = 'mp-p0-' + [Guid]::NewGuid().ToString('N')
    $arguments = @(
        '-NoProfile', '-File', (Join-Path $PSScriptRoot 'run-unattended-test.ps1'),
        '-ScenarioId', 'MULTIPLAYER-P0', '-MultiplayerProbePath', $input,
        '-EvidenceDirectory', $peer, '-HeadlessInstance', $instance,
        '-HeadlessExecutionMode', 'parallel', '-HeadlessMemoryReservationMiB', '1536',
        '-TimeoutSeconds', '120', '-ExitOnComplete', '-CleanupInstanceOnExit'
    )
    $processes += Start-Process -FilePath 'pwsh' -ArgumentList $arguments `
        -WorkingDirectory $repositoryRoot -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $peer 'stdout.txt') `
        -RedirectStandardError (Join-Path $peer 'stderr.txt') -PassThru
}

$processes | Wait-Process
foreach ($seat in 0..1) {
    $processes[$seat].Refresh()
    $resultPath = Join-Path $session "peer-$seat/result.json"
    if ($processes[$seat].ExitCode -ne 0 -or ![IO.File]::Exists($resultPath)) {
        throw "ENet peer $seat failed; inspect $session/peer-$seat."
    }
    $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    if ($result.status -ne 'Passed') {
        throw "ENet peer $seat reported $($result.status); inspect $session/peer-$seat."
    }
}
Write-Output "MULTIPLAYER_P0_ENET_OK evidence=$session peers=2"
