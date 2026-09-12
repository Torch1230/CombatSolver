# No game starts. Run in a disposable OS profile (XDG_DATA_HOME on Linux).
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'headless-runtime.ps1')
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('headless-pool-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
$contexts = [Collections.Generic.List[object]]::new()
$base = $null
function Assert-Pool([bool]$Value, [string]$Message) { if (-not $Value) { throw $Message } }
try {
    $base = New-HeadlessRuntimeContext (Join-Path $fixture 'repo') (Join-Path $fixture 'source') '' parallel 1 1 1
    $a = Select-HeadlessPoolContext $base
    $contexts.Add($a)
    $b = Select-HeadlessPoolContext $base
    $contexts.Add($b)
    Assert-Pool ($a.Instance -eq $base.Instance -and $b.Instance -eq ($base.Instance + '-2')) 'busy slot did not select second slot'
    $timedOut = $false
    try { $unexpected = Select-HeadlessPoolContext $base; $contexts.Add($unexpected) }
    catch { if ($_.Exception.Message -notlike '*pool busy*') { throw }; $timedOut = $true }
    Assert-Pool $timedOut 'full pool did not queue/timeout'
    $a.PoolLauncherLock.Dispose()
    $c = Select-HeadlessPoolContext $base
    $contexts.Add($c)
    Assert-Pool ($c.Instance -eq $a.Instance) 'idle first slot not reused'
    $matrix = [IO.File]::Open((Join-Path $c.Root 'matrix.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
    try {
        $c.PoolLauncherLock.Dispose()
        $b.PoolLauncherLock.Dispose()
        $d = Select-HeadlessPoolContext $base
        $contexts.Add($d)
        Assert-Pool ($d.Instance -eq $b.Instance) 'matrix gap was stolen'
    } finally { $matrix.Dispose() }
    Write-Host 'HEADLESS_POWERSHELL_POOL_PASSED reuse, occupied fallback, bounded queue, matrix gaps'
} finally {
    foreach ($context in $contexts) { $context.PoolLauncherLock.Dispose() }
    # All roots are derived from this invocation unique repository path.
    if ($null -ne $base) {
        foreach ($path in @($base.Root, ($base.Root + '-2'))) {
            if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
        }
    }
    Remove-Item -LiteralPath $fixture -Recurse -Force
}
