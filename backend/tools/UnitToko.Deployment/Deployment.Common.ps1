#requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# All failures use fixed codes. Never include a supplied value, file body or exception.
function Stop-Deployment([string] $Code) { throw "unit_toko_deployment:$Code" }
function Read-DeploymentJson([string] $Path) {
    try {
        $item = Get-Item -LiteralPath $Path -Force
        if ($item.PSIsContainer -or $item.Length -gt 1048576) { Stop-Deployment 'config_size_invalid' }
        $value = [IO.File]::ReadAllText($item.FullName) | ConvertFrom-Json
        if ($null -eq $value -or $value -isnot [pscustomobject]) { Stop-Deployment 'config_object_required' }
        return $value
    } catch { Stop-Deployment 'config_unreadable_or_invalid' }
}
function Require-DeploymentText($Value) {
    if ($Value -isnot [string] -or [string]::IsNullOrWhiteSpace($Value) -or $Value.Length -gt 4096 -or
        $Value -match '[\x00-\x1f\x7f]' -or $Value -match 'REQUIRED_|@@|<[^>]+>') { Stop-Deployment 'config_value_missing_or_invalid' }
    return $Value
}
function Require-DeploymentOrigin($Value) {
    $text = Require-DeploymentText $Value
    $uri = $null
    if (-not [Uri]::TryCreate($text, [UriKind]::Absolute, [ref] $uri) -or $uri.Scheme -cne 'https' -or
        -not $uri.IsDefaultPort -or $uri.UserInfo -ne '' -or $uri.Query -ne '' -or $uri.Fragment -ne '' -or
        $uri.HostNameType -ne [UriHostNameType]::Dns -or $uri.IsLoopback -or
        $text -cne $uri.GetLeftPart([UriPartial]::Authority) -or
        $uri.Host -match '(^|\.)(example\.(com|org|net)|invalid|localhost|test|example)$') { Stop-Deployment 'https_origin_invalid' }
    return $uri
}
function Require-DeploymentPath([string] $Path, [switch] $Existing) {
    if ($Path -notmatch '^[A-Za-z]:[\\/]' -or $Path.StartsWith('\\') -or $Path -match '[\x00-\x1f\x7f{}$";]') {
        Stop-Deployment 'local_absolute_path_required'
    }
    $full = [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if ($full.Length -le 3 -or $full.Substring(2).Contains(':')) { Stop-Deployment 'unsafe_path' }
    $cursor = $full
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            $entry = Get-Item -LiteralPath $cursor -Force
            if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { Stop-Deployment 'reparse_path_rejected' }
        }
        $parent = [IO.Directory]::GetParent($cursor)
        if ($null -eq $parent) { break }
        $cursor = $parent.FullName
    }
    if ($Existing -and -not (Test-Path -LiteralPath $full)) { Stop-Deployment 'path_missing' }
    return $full
}
function Test-DeploymentWithin([string] $Child, [string] $Parent) {
    return $Child.Equals($Parent, [StringComparison]::OrdinalIgnoreCase) -or
        $Child.StartsWith($Parent.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)
}
function Get-DeploymentEnvironment($Value, [string] $Prefix = '') {
    $pairs = @{}
    if ($Value -is [pscustomobject]) {
        foreach ($property in $Value.PSObject.Properties) {
            if ($property.Name -notmatch '^[A-Za-z][A-Za-z0-9_.]*$' -or $property.Name.Contains('__')) { Stop-Deployment 'config_key_invalid' }
            $name = if ($Prefix) { $Prefix + '__' + $property.Name } else { $property.Name }
            $nested = Get-DeploymentEnvironment $property.Value $name
            foreach ($key in $nested.Keys) { $pairs[$key] = $nested[$key] }
        }
    } elseif ($Value -is [array]) {
        for ($index = 0; $index -lt $Value.Length; $index++) {
            $nested = Get-DeploymentEnvironment $Value[$index] ($Prefix + '__' + $index)
            foreach ($key in $nested.Keys) { $pairs[$key] = $nested[$key] }
        }
    } elseif ($Value -is [bool]) { $pairs[$Prefix] = $Value.ToString().ToLowerInvariant() }
    elseif ($Value -is [string]) { $pairs[$Prefix] = Require-DeploymentText $Value }
    elseif ($Value -is [ValueType]) { $pairs[$Prefix] = [Convert]::ToString($Value, [Globalization.CultureInfo]::InvariantCulture) }
    else { Stop-Deployment 'config_null_or_type_invalid' }
    return $pairs
}
function Get-DeploymentConfig([string] $Path, [ValidateSet('api','worker')] [string] $Component,
    [ValidateSet('release1','full')] [string] $ExpectedProfile = 'release1') {
    $config = Read-DeploymentJson (Require-DeploymentPath $Path -Existing)
    $allowed = if ($Component -eq 'api') { @('Mongo','Identity','Integrity','Evidence','Release','Logging','AllowedHosts','ASPNETCORE_ENVIRONMENT','ASPNETCORE_URLS') }
        else { @('Mongo','Evidence','Notifications','Logging','DOTNET_ENVIRONMENT') }
    foreach ($property in $config.PSObject.Properties) {
        if ($property.Name -cnotin $allowed) { Stop-Deployment 'unsupported_config_section' }
    }
    $pairs = Get-DeploymentEnvironment $config
    foreach ($key in @('Mongo__Uri','Mongo__Database')) {
        if (-not $pairs.ContainsKey($key)) { Stop-Deployment 'mongo_configuration_missing' }
    }
    if ($pairs['Mongo__Uri'] -notmatch '^mongodb(\+srv)?://' -or
        $pairs['Mongo__Database'] -match '^(admin|local|config)$|[\s/\\."$]' -or $pairs['Mongo__Database'].Length -gt 63) {
        Stop-Deployment 'dedicated_mongo_configuration_invalid'
    }
    # No runtime output should disclose URLs, raw requests, cookies or provider payloads.
    if (-not $pairs.ContainsKey('Logging__LogLevel__Default') -or $pairs['Logging__LogLevel__Default'] -cne 'None') {
        Stop-Deployment 'private_console_logging_required'
    }
    foreach ($key in $pairs.Keys) {
        if ($key.StartsWith('Logging__LogLevel__') -and $pairs[$key] -cne 'None') { Stop-Deployment 'private_console_logging_required' }
    }
    $frameworkCategory = if ($Component -eq 'api') { 'Microsoft.AspNetCore' } else { 'Microsoft.Hosting.Lifetime' }
    if (-not $pairs.ContainsKey('Logging__LogLevel__' + $frameworkCategory)) { Stop-Deployment 'published_framework_logging_override_required' }
    if ($Component -eq 'api') {
        foreach ($key in @('Identity__Auth0__Enabled','Identity__Auth0__Issuer','Identity__Auth0__PublicOrigin','Identity__Auth0__ClientId',
            'Identity__Auth0__ClientSecret','Identity__Auth0__ConnectionId','Identity__Auth0__ConnectionName','Identity__Auth0__ClaimsNamespace',
            'Identity__Auth0__AssurancePolicyVersion','Identity__Keys__Directory','Identity__Keys__ApplicationName',
            'Identity__Keys__CertificatePath','Identity__Keys__CertificatePassword','Identity__TrustedProxies__0',
            'Release__Profile','ASPNETCORE_ENVIRONMENT','ASPNETCORE_URLS','AllowedHosts')) {
            if (-not $pairs.ContainsKey($key)) { Stop-Deployment 'identity_or_host_configuration_missing' }
        }
        $origin = Require-DeploymentOrigin $pairs['Identity__Auth0__PublicOrigin']
        if ($pairs['Identity__Auth0__Enabled'] -cne 'true' -or $pairs['Release__Profile'] -cne $ExpectedProfile -or
            $pairs['ASPNETCORE_ENVIRONMENT'] -cne 'Production' -or $pairs['ASPNETCORE_URLS'] -cne 'http://127.0.0.1:5080' -or
            $pairs['AllowedHosts'] -cne $origin.Host -or $pairs['Identity__TrustedProxies__0'] -cne '127.0.0.1' -or
            @($pairs.Keys | Where-Object { $_ -like 'Identity__TrustedProxies__*' }).Count -ne 1) { Stop-Deployment 'single_origin_loopback_policy_required' }
        if (-not $pairs['Identity__Auth0__Issuer'].EndsWith('/')) { Stop-Deployment 'issuer_invalid' }
        $null = Require-DeploymentOrigin $pairs['Identity__Auth0__Issuer'].TrimEnd('/')
        $allowedReturns = @('/','/identity/account','/shop','/catalog/browse','/ordering/requests','/ordering/requests/incoming','/administration/catalog')
        $actualReturns = @($pairs.Keys | Where-Object { $_ -like 'Identity__Auth0__AllowedReturnPaths__*' } | ForEach-Object { $pairs[$_] })
        if ($ExpectedProfile -eq 'release1') {
            if ($actualReturns.Count -ne $allowedReturns.Count -or @($actualReturns | Select-Object -Unique).Count -ne $allowedReturns.Count -or
                @($actualReturns | Where-Object { $_ -cnotin $allowedReturns }).Count -ne 0) { Stop-Deployment 'release1_return_paths_required' }
        } elseif ($actualReturns.Count -eq 0 -or $actualReturns.Count -gt 64 -or '/identity/account' -cnotin $actualReturns -or
            @($actualReturns | Select-Object -Unique).Count -ne $actualReturns.Count -or
            @($actualReturns | Where-Object { $_.Length -gt 200 -or -not $_.StartsWith('/') -or $_ -match '//|[\s\\%?#:]|(^|/)\.\.?(/|$)' }).Count -ne 0) {
            Stop-Deployment 'full_return_paths_invalid'
        }
        $null = Require-DeploymentPath $pairs['Identity__Keys__Directory'] -Existing
        $certificate = Require-DeploymentPath $pairs['Identity__Keys__CertificatePath'] -Existing
        if ((Get-Item -LiteralPath $certificate).PSIsContainer -or (Get-Item -LiteralPath $certificate).Length -gt 1048576) { Stop-Deployment 'identity_certificate_file_invalid' }
        if ($ExpectedProfile -eq 'full') {
            # Provenance seals let each request verify sealed history instead of re-proving it; unsealed history would
            # take the slow full proof everywhere. A full deployment requires them and refuses to start without the keys.
            if ($pairs['Integrity__SealsRequired'] -cne 'true') { Stop-Deployment 'provenance_seals_required' }
            if (-not (Test-Path -LiteralPath (Join-Path $pairs['Identity__Keys__Directory'] 'seal-current.txt') -PathType Leaf)) {
                Stop-Deployment 'provenance_seal_keys_not_provisioned'
            }
        }
    } elseif (-not $pairs.ContainsKey('DOTNET_ENVIRONMENT') -or $pairs['DOTNET_ENVIRONMENT'] -cne 'Production') { Stop-Deployment 'worker_environment_invalid' }
    if (@($pairs.Keys | Where-Object { $_ -like 'Evidence__PaymentPdfOcr__*' }).Count -gt 0) { Stop-Deployment 'primary_payment_pdf_not_supported' }
    if ($ExpectedProfile -eq 'full') {
        $groups = if ($Component -eq 'api') { @('PrivateWindows','PrivateVideoWindows','ImageRevalidation') }
            else { @('Validation','ImageRevalidation','PaymentOcr','SupplierDocumentOcr','VideoValidation','PdfValidation','SupplierDocumentPdfOcr','PaymentSupplementPdfOcr') }
        $shape = Read-DeploymentJson (Join-Path $PSScriptRoot ("templates/$Component.full.template.json"))
        foreach ($group in $groups) {
            $actual = $config.Evidence.$group
            $expected = $shape.Evidence.$group
            if ($actual -isnot [pscustomobject] -or @($actual.PSObject.Properties).Count -ne @($expected.PSObject.Properties).Count) {
                Stop-Deployment 'full_runtime_record_shape_invalid'
            }
            foreach ($property in $expected.PSObject.Properties) {
                if ($null -eq $actual.PSObject.Properties[$property.Name]) { Stop-Deployment 'full_runtime_record_shape_invalid' }
                if ($property.Name -in @('Limits','Decoder','Scanner','Engine','Raster','ClaimPolicy') -and $actual.($property.Name) -isnot [pscustomobject]) {
                    Stop-Deployment 'complete_typed_runtime_object_required'
                }
            }
            foreach ($suffix in @('Root','StoreId','TrustedOperatorSids__0')) {
                if (-not $pairs.ContainsKey("Evidence__${group}__${suffix}")) { Stop-Deployment 'full_private_runtime_configuration_missing' }
            }
            if ($group -notin @('PrivateWindows','PrivateVideoWindows')) {
                foreach ($suffix in @('ConsumerId','HostAcceptanceReference')) {
                    if (-not $pairs.ContainsKey("Evidence__${group}__${suffix}")) { Stop-Deployment 'full_private_runtime_configuration_missing' }
                }
            }
        }
    }
    return $pairs
}
function Get-DeploymentGateway([string] $Path) {
    $config = Read-DeploymentJson (Require-DeploymentPath $Path -Existing)
    $keys = @('PublicOrigin','FrontendOrigin','FrontendCommit','FrontendPublicOrigin','FrontendApiOrigin','FrontendProfile',
        'TlsCertificatePath','TlsPrivateKeyPath','CaddyBinaryPath','CaddySha256','CaddyVersion')
    if (@($config.PSObject.Properties).Count -ne $keys.Count) { Stop-Deployment 'gateway_config_shape_invalid' }
    foreach ($name in $keys) {
        if ($null -eq $config.PSObject.Properties[$name]) { Stop-Deployment 'gateway_config_shape_invalid' }
        $null = Require-DeploymentText $config.$name
    }
    $public = Require-DeploymentOrigin $config.PublicOrigin
    $frontend = Require-DeploymentOrigin $config.FrontendOrigin
    if ($frontend.Host -notmatch '^[a-z0-9-]+\.vercel\.app$' -or $frontend.Host -eq $public.Host -or
        $config.FrontendPublicOrigin -cne $config.PublicOrigin -or $config.FrontendApiOrigin -cne $config.PublicOrigin -or
        $config.FrontendProfile -cnotin @('release1','full') -or
        $config.FrontendCommit -notmatch '^[0-9a-f]{40}$' -or $config.CaddySha256 -notmatch '^[0-9a-fA-F]{64}$' -or
        $config.CaddyVersion -notmatch '^2\.[0-9]+\.[0-9]+$' -or [Version]$config.CaddyVersion -lt [Version]'2.11.0') { Stop-Deployment 'gateway_binding_invalid' }
    foreach ($name in @('TlsCertificatePath','TlsPrivateKeyPath','CaddyBinaryPath')) { $null = Require-DeploymentPath $config.$name -Existing }
    if ((Get-FileHash -LiteralPath $config.CaddyBinaryPath -Algorithm SHA256).Hash -cne $config.CaddySha256.ToUpperInvariant()) { Stop-Deployment 'gateway_binary_hash_mismatch' }
    return $config
}
function Assert-DeploymentBundle([string] $Directory) {
    $root = Require-DeploymentPath $Directory -Existing
    $manifest = Read-DeploymentJson (Join-Path $root 'manifest.json')
    if ($manifest.formatVersion -ne 1 -or $manifest.sourceCommit -notmatch '^[0-9a-f]{40}$' -or
        $manifest.runtime -cne 'win-x64' -or $manifest.selfContained -ne $false -or $manifest.releaseProfile -cnotin @('release1','full')) {
        Stop-Deployment 'bundle_manifest_invalid'
    }
    $seen = @{}
    foreach ($file in $manifest.files) {
        if ($file.path -isnot [string] -or $file.path -match '(^|/)\.\.(/|$)|^[\/]|:|\\' -or
            $file.sha256 -notmatch '^[0-9A-F]{64}$' -or $seen.ContainsKey($file.path)) { Stop-Deployment 'bundle_file_invalid' }
        $path = Require-DeploymentPath (Join-Path $root $file.path.Replace('/', '\')) -Existing
        if (-not (Test-DeploymentWithin $path $root) -or (Get-Item -LiteralPath $path).PSIsContainer -or
            (Get-Item -LiteralPath $path).Length -ne $file.length -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne $file.sha256) { Stop-Deployment 'bundle_hash_mismatch' }
        $seen[$file.path] = $true
    }
    foreach ($file in Get-ChildItem -LiteralPath $root -File -Recurse -Force) {
        $relative = $file.FullName.Substring($root.Length + 1).Replace('\', '/')
        if ($relative -cne 'manifest.json' -and -not $seen.ContainsKey($relative)) { Stop-Deployment 'unlisted_bundle_file' }
    }
    foreach ($component in @('api','worker','image-decoder')) {
        $name = switch ($component) { 'api' { 'UnitToko.Api' } 'worker' { 'UnitToko.Worker' } 'image-decoder' { 'UnitToko.ImageDecoder' } }
        foreach ($extension in @('.exe','.dll','.deps.json','.runtimeconfig.json')) {
            if (-not $seen.ContainsKey("$component/$name$extension")) { Stop-Deployment 'incomplete_publish_output' }
        }
    }
    # The private decoder pins and loads this exact relative native-library path.
    # A RID publish's flattened DLL alone cannot satisfy the actual consumer.
    foreach ($decoderFile in @('Magick.NET-Q8-AnyCPU.dll','Magick.NET.Core.dll','policy.xml',
        'runtimes/win-x64/native/Magick.Native-Q8-x64.dll',
        'third-party/Magick.NET-Notice.txt','third-party/Magick.NET-Core-Copyright.txt')) {
        if (-not $seen.ContainsKey("image-decoder/$decoderFile")) { Stop-Deployment 'incomplete_decoder_runtime_output' }
    }
    # Additional named Kestrel endpoints would override the required loopback URL.
    foreach ($settingsFile in Get-ChildItem -LiteralPath (Join-Path $root 'api') -Filter 'appsettings*.json' -File) {
        $settings = Read-DeploymentJson $settingsFile.FullName
        if ($null -ne $settings.PSObject.Properties['Kestrel'] -or $null -ne $settings.PSObject.Properties['Urls']) {
            Stop-Deployment 'published_listener_override_rejected'
        }
    }
    return $manifest
}
