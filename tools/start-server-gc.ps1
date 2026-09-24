#requires -Version 7.4
param(
    [Parameter(Mandatory)][string]$GameExecutable,
    [Parameter(ValueFromRemainingArguments)][string[]]$GameArguments = @()
)
$ErrorActionPreference = 'Stop'
$executable = (Get-Item -LiteralPath $GameExecutable -ErrorAction Stop).FullName
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'Expected the game executable.' }
$name = [IO.Path]::GetFileNameWithoutExtension($executable)
foreach ($running in @(Get-Process -Name $name -ErrorAction SilentlyContinue)) {
    if ([string]::Equals($running.Path, $executable, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'This game is already running. Close it before selecting a different runtime profile.'
    }
}
$start = [Diagnostics.ProcessStartInfo]::new($executable)
$start.UseShellExecute = $false
$start.WorkingDirectory = [IO.Path]::GetDirectoryName($executable)
$start.Environment['DOTNET_gcServer'] = '1'
$start.Environment['COMPlus_gcServer'] = '1'
$start.Environment['COMBATSOLVER_RUNTIME_PROFILE'] = 'server-generational'
foreach ($argument in $GameArguments) { $start.ArgumentList.Add($argument) }
Write-Host 'Starting with server-generational GC for this process; saved solver settings are unchanged.'
$process = [Diagnostics.Process]::Start($start)
try {
    $process.WaitForExit()
    $exitCode = $process.ExitCode
} finally { $process.Dispose() }
exit $exitCode
