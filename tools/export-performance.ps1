param([Parameter(Mandatory)][string]$SessionDirectory)
$ErrorActionPreference = 'Stop'
$directory = [IO.Path]::GetFullPath($SessionDirectory)
$health = Get-Content -LiteralPath (Join-Path $directory 'collector-health.json') -Raw | ConvertFrom-Json
if ($health.status -ne 'complete') { throw "Recording is not complete: $($health.status). Exit the game and wait for collection to finish. Keep this directory if collection failed." }
$failures = Get-ChildItem -LiteralPath $directory -Filter '*FAILED.txt' -File
if ($failures.Count -gt 0) { throw 'The session contains a failure marker. Preserve the original directory for diagnosis.' }
$frames = 0
$inventories = 0
$ended = $false
$reader = [IO.File]::OpenText((Join-Path $directory 'timeline.jsonl'))
try {
    while ($null -ne ($line = $reader.ReadLine())) {
        $row = $line | ConvertFrom-Json
        if ($row.kind -eq 'recorder_failed' -or ($null -ne $row.dropped -and $row.dropped -gt 0)) {
            throw 'The timeline reports recording failure or dropped records. Preserve the original directory.'
        }
        if ($row.kind -eq 'frames') { $frames++ }
        if ($row.kind -eq 'inventory') { $inventories++ }
        if ($row.kind -eq 'writer_end') { $ended = $true }
    }
}
finally { $reader.Dispose() }
if (!$ended -or $frames -eq 0 -or $inventories -eq 0) { throw 'The timeline is missing finalization, frame data, or mod inventory. Preserve the original directory.' }
$destination = $directory.TrimEnd('\', '/') + '.zip'
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($directory, $destination, [IO.Compression.CompressionLevel]::Fastest, $false)
Write-Output $destination
