#requires -Version 7.0

param(
    [Parameter(Mandatory = $true)]
    [string[]]$Key,

    [string]$PckPath = 'D:\Steam\steamapps\common\Slay the Spire 2\SlayTheSpire2.pck'
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $PckPath -PathType Leaf)) {
    throw "找不到游戏 PCK：$PckPath"
}

$stream = [System.IO.File]::OpenRead($PckPath)
$reader = [System.IO.BinaryReader]::new($stream, [System.Text.Encoding]::UTF8, $true)
try {
    if ([System.Text.Encoding]::ASCII.GetString($reader.ReadBytes(4)) -ne 'GDPC') {
        throw "不是有效的 Godot PCK：$PckPath"
    }

    $stream.Position = 24
    $fileBase = $reader.ReadInt64()
    $directoryOffset = $reader.ReadInt64()
    $stream.Position = $directoryOffset
    $fileCount = $reader.ReadUInt32()
    $zhsEntries = @()
    for ($index = 0; $index -lt $fileCount; $index++) {
        $pathLength = $reader.ReadUInt32()
        $entryPath = [System.Text.Encoding]::UTF8.GetString($reader.ReadBytes($pathLength)).TrimEnd([char]0)
        $offset = $reader.ReadUInt64()
        $size = $reader.ReadUInt64()
        $null = $reader.ReadBytes(16)
        $flags = $reader.ReadUInt32()
        if ($entryPath -match '^localization/zhs/[^/]+\.json$') {
            if ($flags -ne 0) {
                throw "简中本地化资源使用了不支持的 PCK 标志：$entryPath flags=$flags"
            }
            $zhsEntries += [pscustomobject]@{
                Path = $entryPath
                Offset = $offset
                Size = $size
            }
        }
    }

    if ($zhsEntries.Count -eq 0) {
        throw 'PCK 中没有找到 localization/zhs/*.json。'
    }

    $requested = [System.Collections.Generic.HashSet[string]]::new($Key, [System.StringComparer]::Ordinal)
    $localized = @{}
    foreach ($entry in $zhsEntries) {
        $stream.Position = $fileBase + [long]$entry.Offset
        $bytes = $reader.ReadBytes([int]$entry.Size)
        if ($bytes.Length -ne $entry.Size) {
            throw "读取简中本地化资源不完整：$($entry.Path)"
        }
        $table = [System.Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json -AsHashtable
        foreach ($entryKey in $requested) {
            if (-not $table.ContainsKey($entryKey)) {
                continue
            }
            if ($localized.ContainsKey($entryKey)) {
                throw "简中本地化键重复：$entryKey"
            }
            $localized[$entryKey] = $table[$entryKey]
        }
    }
}
finally {
    $reader.Dispose()
    $stream.Dispose()
}

foreach ($entryKey in $Key) {
    [pscustomobject]@{
        Key = $entryKey
        Text = $localized[$entryKey]
    }
}
