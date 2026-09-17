Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression

function New-QuarkReleaseBundle {
    param(
        [Parameter(Mandatory)]
        [string]$MinimalReleaseZip,

        [Parameter(Mandatory)]
        [string]$OutputPath,

        [long]$MinimumBytesExclusive = 15MB
    )

    $paddingEntryName = 'QUARK_UPLOAD_PADDING.bin'
    $temporaryPath = "$OutputPath.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        Copy-Item -LiteralPath $MinimalReleaseZip -Destination $temporaryPath
        $bundle = Get-Item -LiteralPath $temporaryPath
        if ($bundle.Length -le $MinimumBytesExclusive) {
            $paddingLength = $MinimumBytesExclusive - $bundle.Length + 1
            $archive = [System.IO.Compression.ZipFile]::Open(
                $temporaryPath,
                [System.IO.Compression.ZipArchiveMode]::Update)
            try {
                if ($null -ne $archive.GetEntry($paddingEntryName)) {
                    throw "夸克打包版中已存在填充条目：$paddingEntryName"
                }
                $paddingEntry = $archive.CreateEntry(
                    $paddingEntryName,
                    [System.IO.Compression.CompressionLevel]::NoCompression)
                $paddingStream = $paddingEntry.Open()
                try {
                    $buffer = [byte[]]::new(1MB)
                    $remaining = $paddingLength
                    while ($remaining -gt 0) {
                        $writeLength = [Math]::Min($buffer.Length, $remaining)
                        $paddingStream.Write($buffer, 0, $writeLength)
                        $remaining -= $writeLength
                    }
                }
                finally {
                    $paddingStream.Dispose()
                }
            }
            finally {
                $archive.Dispose()
            }
            $bundle = Get-Item -LiteralPath $temporaryPath
        }

        if ($bundle.Length -le $MinimumBytesExclusive) {
            throw "夸克打包版必须超过 $MinimumBytesExclusive 字节，实际为 $($bundle.Length) 字节。"
        }
        Move-Item -LiteralPath $temporaryPath -Destination $OutputPath -Force
        return Get-Item -LiteralPath $OutputPath
    }
    catch {
        Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
        throw
    }
}
