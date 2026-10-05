#requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory)] [string] $GatewayConfigPath, [Parameter(Mandatory)] [string] $OutputPath)
. (Join-Path $PSScriptRoot 'Deployment.Common.ps1')

# Render only. Never invokes Caddy, opens a listener, obtains TLS or changes DNS.
try {
    $settings = Get-DeploymentGateway $GatewayConfigPath
    $destination = Require-DeploymentPath $OutputPath
    if (Test-Path -LiteralPath $destination) { Stop-Deployment 'new_gateway_file_required' }
    $null = Require-DeploymentPath ([IO.Directory]::GetParent($destination).FullName) -Existing
    $values = @{
        '@@PUBLIC_HOST@@'=(Require-DeploymentOrigin $settings.PublicOrigin).Host;
        '@@FRONTEND_HOST@@'=(Require-DeploymentOrigin $settings.FrontendOrigin).Host;
        '@@TLS_CERTIFICATE@@'=$settings.TlsCertificatePath.Replace('\','/');
        '@@TLS_PRIVATE_KEY@@'=$settings.TlsPrivateKeyPath.Replace('\','/')
    }
    $content = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'templates/Caddyfile.template'))
    foreach ($key in $values.Keys) { $content = $content.Replace($key, $values[$key]) }
    if ($content.Contains('@@')) { Stop-Deployment 'gateway_template_incomplete' }
    [IO.File]::WriteAllText($destination, $content, [Text.UTF8Encoding]::new($false))
    Write-Output 'unit_toko_deployment:gateway_rendered_validation_and_host_acceptance_pending'
} catch {
    if ($_.Exception.Message -like 'unit_toko_deployment:*') { throw $_.Exception.Message }
    Stop-Deployment 'gateway_render_failed'
}
