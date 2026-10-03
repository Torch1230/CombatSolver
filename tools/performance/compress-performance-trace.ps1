param([Parameter(Mandatory)][string]$TracePath)
$ErrorActionPreference = 'Stop'
[Diagnostics.Process]::GetCurrentProcess().PriorityClass = [Diagnostics.ProcessPriorityClass]::BelowNormal
$source = [IO.Path]::GetFullPath($TracePath)
if ([IO.Path]::GetExtension($source) -ne '.nettrace') { throw 'Only a completed .nettrace segment can be compressed.' }
$destination = $source + '.zip'
$temporary = $destination + '.tmp'
$started = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
$originalBytes = ([IO.FileInfo]::new($source)).Length
Add-Type -AssemblyName System.IO.Compression
$inputStream = [IO.File]::Open($source, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
try {
    $outputStream = [IO.File]::Open($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $archive = [IO.Compression.ZipArchive]::new($outputStream, [IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            $entry = $archive.CreateEntry([IO.Path]::GetFileName($source), [IO.Compression.CompressionLevel]::Fastest)
            $entryStream = $entry.Open()
            try { $inputStream.CopyTo($entryStream) }
            finally { $entryStream.Dispose() }
        }
        finally { $archive.Dispose() }
        $outputStream.Flush($true)
    }
    finally { $outputStream.Dispose() }
}
finally { $inputStream.Dispose() }
# Commit the finished archive before removing its redundant raw source. Failure keeps the raw trace.
[IO.File]::Move($temporary, $destination)
[IO.File]::Delete($source)
$result = @{ originalBytes = $originalBytes; compressedBytes = ([IO.FileInfo]::new($destination)).Length;
    archive = $destination; startedUtcMs = $started; finishedUtcMs = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() }
$json = $result | ConvertTo-Json -Compress
[IO.File]::WriteAllText($source + '.compression.json', $json)
Write-Output $json
