function Assert-ReleaseConnectionMetadata {
    param([Parameter(Mandatory)][string]$DllPath)

    $assembly = [System.Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $DllPath).Path)
    $metadata = @{}
    foreach ($attribute in $assembly.GetCustomAttributesData()) {
        if ($attribute.AttributeType.FullName -ne 'System.Reflection.AssemblyMetadataAttribute') {
            continue
        }
        $key = [string]$attribute.ConstructorArguments[0].Value
        $metadata[$key] = [string]$attribute.ConstructorArguments[1].Value
    }

    foreach ($key in @(
        'PresenceEndpoint',
        'PresenceCertificateSha256',
        'ShowcaseEndpoint',
        'ShowcaseUploadToken',
        'ShowcaseCertificateSha256'
    )) {
        if ([string]::IsNullOrWhiteSpace($metadata[$key])) {
            throw "发布 DLL 缺少连接元数据：$key"
        }
    }
    foreach ($key in @('PresenceEndpoint', 'ShowcaseEndpoint')) {
        $address = $null
        $validAddress = [Uri]::TryCreate($metadata[$key], [UriKind]::Absolute, [ref]$address)
        if (-not $validAddress -or $address.Scheme -ne [Uri]::UriSchemeHttps) {
            throw "发布 DLL 的 $key 必须使用 HTTPS。"
        }
    }
    foreach ($key in @('PresenceCertificateSha256', 'ShowcaseCertificateSha256')) {
        if ($metadata[$key] -cnotmatch '^[0-9A-Fa-f]{64}$') {
            throw "发布 DLL 的 $key 必须是 SHA-256 证书指纹。"
        }
    }
}
