#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $BundleDirectory,
    [Parameter(Mandatory)] [string] $ApiConfigPath,
    [Parameter(Mandatory)] [string] $WorkerConfigPath,
    [Parameter(Mandatory)] [string] $GatewayConfigPath
)
. (Join-Path $PSScriptRoot 'Deployment.Common.ps1')

# No sockets, API/worker execution, service changes or data writes. The only
# executable probe is dotnet --list-runtimes; it does not start the application.
try {
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT -or -not [Environment]::Is64BitOperatingSystem) {
        Stop-Deployment 'windows_x64_host_required'
    }
    $manifest = Assert-DeploymentBundle $BundleDirectory
    $api = Get-DeploymentConfig $ApiConfigPath 'api' $manifest.releaseProfile
    $worker = Get-DeploymentConfig $WorkerConfigPath 'worker' $manifest.releaseProfile
    $gateway = Get-DeploymentGateway $GatewayConfigPath
    if ($api['Mongo__Uri'] -cne $worker['Mongo__Uri'] -or $api['Mongo__Database'] -cne $worker['Mongo__Database']) { Stop-Deployment 'api_worker_database_binding_mismatch' }
    if ($api['Identity__Auth0__PublicOrigin'] -cne $gateway.PublicOrigin -or $manifest.sourceCommit -cne $gateway.FrontendCommit -or
        $manifest.releaseProfile -cne $gateway.FrontendProfile) {
        Stop-Deployment 'frontend_api_origin_or_commit_mismatch'
    }
    foreach ($configPath in @($ApiConfigPath,$WorkerConfigPath,$GatewayConfigPath)) {
        $full = Require-DeploymentPath $configPath -Existing
        if (Test-DeploymentWithin $full (Require-DeploymentPath $BundleDirectory -Existing)) { Stop-Deployment 'private_configuration_must_be_outside_bundle' }
    }
    foreach ($name in @('Identity__Keys__Directory','Identity__Keys__CertificatePath')) {
        $full = Require-DeploymentPath $api[$name] -Existing
        if (Test-DeploymentWithin $full (Require-DeploymentPath $BundleDirectory -Existing)) { Stop-Deployment 'persistent_keys_must_be_outside_bundle' }
        $drive = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($full))
        if ($drive.DriveType -ne [IO.DriveType]::Fixed -or $drive.DriveFormat -cne 'NTFS') { Stop-Deployment 'fixed_ntfs_required' }
    }
    foreach ($settings in @($api,$worker)) {
        foreach ($name in @($settings.Keys | Where-Object { $_ -match '^Evidence__[A-Za-z]+__Root$' })) {
            $full = Require-DeploymentPath $settings[$name] -Existing
            if (Test-DeploymentWithin $full (Require-DeploymentPath $BundleDirectory -Existing)) { Stop-Deployment 'private_evidence_must_be_outside_bundle' }
            $drive = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($full))
            if ($drive.DriveType -ne [IO.DriveType]::Fixed -or $drive.DriveFormat -cne 'NTFS') { Stop-Deployment 'fixed_ntfs_required' }
        }
        foreach ($name in @($settings.Keys | Where-Object { $_ -match '^Evidence__.+__Files__[0-9]+__Path$' })) {
            $hashName = $name.Substring(0, $name.Length - 4) + 'Sha256'
            if (-not $settings.ContainsKey($hashName) -or $settings[$hashName] -notmatch '^[a-fA-F0-9]{64}$') { Stop-Deployment 'native_hash_inventory_incomplete' }
            $file = Require-DeploymentPath $settings[$name] -Existing
            if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -cne $settings[$hashName].ToUpperInvariant()) { Stop-Deployment 'native_hash_inventory_mismatch' }
        }
    }
    if ($manifest.releaseProfile -eq 'full') {
        foreach ($group in @('Validation','ImageRevalidation','PaymentOcr','SupplierDocumentOcr','VideoValidation','PdfValidation','SupplierDocumentPdfOcr','PaymentSupplementPdfOcr')) {
            $owner = if ($group -eq 'VideoValidation') { 'PrivateVideoWindows' } else { 'PrivateWindows' }
            foreach ($suffix in @('Root','StoreId')) {
                if ($worker["Evidence__${group}__${suffix}"] -cne $api["Evidence__${owner}__${suffix}"]) { Stop-Deployment 'worker_api_private_store_binding_mismatch' }
            }
        }
        foreach ($suffix in @('Root','StoreId')) {
            if ($api["Evidence__ImageRevalidation__${suffix}"] -cne $api["Evidence__PrivateWindows__${suffix}"]) { Stop-Deployment 'api_revalidation_store_binding_mismatch' }
        }
    }
    $runtimes = @(& dotnet --list-runtimes 2>$null)
    if ($LASTEXITCODE -ne 0 -or @($runtimes | Where-Object { $_ -match '^Microsoft\.NETCore\.App 10\.' }).Count -eq 0 -or
        @($runtimes | Where-Object { $_ -match '^Microsoft\.AspNetCore\.App 10\.' }).Count -eq 0) { Stop-Deployment 'net10_and_aspnet10_runtimes_required' }
    Write-Output 'unit_toko_deployment:offline_preflight_passed_real_host_acceptance_pending'
} catch {
    if ($_.Exception.Message -like 'unit_toko_deployment:*') { throw $_.Exception.Message }
    Stop-Deployment 'offline_preflight_failed'
}
