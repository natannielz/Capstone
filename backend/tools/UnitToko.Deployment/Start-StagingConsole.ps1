#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidateSet('api','worker')] [string] $Component,
    [Parameter(Mandatory)] [string] $BundleDirectory,
    [Parameter(Mandatory)] [string] $ConfigPath,
    [Parameter(Mandatory)] [ValidatePattern('^unit_toko_staging_[a-z0-9][a-z0-9_]{0,45}$')] [string] $StagingDatabase,
    [Parameter(Mandatory)] [ValidateSet('ISOLATED_STAGING_NO_REAL_DATA')] [string] $StagingAcknowledgement
)
. (Join-Path $PSScriptRoot 'Deployment.Common.ps1')

# Explicit foreground staging run only. There is no migration or service-install option.
# Operator must first complete Test-WindowsDeployment and authorize the isolated environment.
$previous = @{}
try {
    $manifest = Assert-DeploymentBundle $BundleDirectory
    $settings = Get-DeploymentConfig $ConfigPath $Component $manifest.releaseProfile
    if ($settings['Mongo__Database'] -cne $StagingDatabase) { Stop-Deployment 'dedicated_declared_staging_database_required' }
    foreach ($key in $settings.Keys) {
        if ($key -match '^(Notifications|Delivery)__' -and $key -match '__Enabled$' -and $settings[$key] -cne 'false') {
            Stop-Deployment 'staging_notifications_and_delivery_must_be_disabled'
        }
    }
    # The current worker's only notification flag must be disabled even when omitted.
    $settings['Notifications__PaymentReviewInApp__Enabled'] = 'false'
    $root = Require-DeploymentPath $BundleDirectory -Existing
    if (Test-DeploymentWithin (Require-DeploymentPath $ConfigPath -Existing) $root) { Stop-Deployment 'private_configuration_must_be_outside_bundle' }
    foreach ($entry in Get-ChildItem Env:) {
        if ($entry.Name -match '^(Mongo__|Identity__|Evidence__|Notifications__|Delivery__|Release__|Logging__|Kestrel__|AllowedHosts$|ASPNETCORE_|DOTNET_ENVIRONMENT$|FORWARDEDHEADERS_ENABLED$)') {
            $previous[$entry.Name] = $entry.Value
            [Environment]::SetEnvironmentVariable($entry.Name, $null, 'Process')
        }
    }
    foreach ($key in $settings.Keys) {
        if (-not $previous.ContainsKey($key)) { $previous[$key] = $null }
        [Environment]::SetEnvironmentVariable($key, $settings[$key], 'Process')
    }
    $directory = Join-Path $root $Component
    $name = if ($Component -eq 'api') { 'UnitToko.Api.exe' } else { 'UnitToko.Worker.exe' }
    Write-Output 'unit_toko_deployment:staging_console_starting_no_hosted_acceptance_claim'
    $null = & (Join-Path $directory $name) --contentRoot $directory 2>&1
    if ($LASTEXITCODE -ne 0) { Stop-Deployment 'staging_process_failed' }
} catch {
    if ($_.Exception.Message -like 'unit_toko_deployment:*') { throw $_.Exception.Message }
    Stop-Deployment 'staging_console_failed'
} finally {
    foreach ($key in $previous.Keys) { [Environment]::SetEnvironmentVariable($key, $previous[$key], 'Process') }
}
