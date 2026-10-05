#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $SourceRoot,
    [Parameter(Mandatory)] [ValidatePattern('^[0-9a-f]{40}$')] [string] $VerifiedCommit,
    [Parameter(Mandatory)] [string] $OutputDirectory,
    [ValidateSet('release1','full')] [string] $Profile = 'release1'
)
. (Join-Path $PSScriptRoot 'Deployment.Common.ps1')

# Explicit offline packaging only. No downloads except normal pinned dotnet restore,
# no host configuration, migration, process startup, service/provider or cutover action.
$source = Require-DeploymentPath $SourceRoot -Existing
$output = Require-DeploymentPath $OutputDirectory
if (Test-DeploymentWithin $output $source) { Stop-Deployment 'bundle_must_be_outside_source' }
if (Test-Path -LiteralPath $output) { Stop-Deployment 'new_bundle_directory_required' }
$parent = [IO.Directory]::GetParent($output).FullName
$null = Require-DeploymentPath $parent -Existing
$backend = Join-Path $source 'backend'
$original = Get-Location
try {
    Set-Location -LiteralPath $source
    $head = @(& git rev-parse --verify HEAD 2>$null)
    if ($LASTEXITCODE -ne 0 -or $head.Count -ne 1 -or $head[0] -cne $VerifiedCommit) { Stop-Deployment 'verified_commit_mismatch' }
    # Only actual .NET/build/package inputs must match HEAD. Original frontend
    # and proposal edits are unrelated and must never be cleaned or reset.
    $inputPaths = @('backend/src','backend/document-processing/local_ocr.py','backend/document-processing/pdf_preprocess.py',
        'backend/global.json','backend/Directory.Build.props','backend/Directory.Build.targets',
        'backend/Directory.Packages.props','backend/NuGet.Config','backend/NuGet.config','backend/tools/UnitToko.Deployment',
        'Directory.Build.props','Directory.Build.targets','Directory.Packages.props','NuGet.Config','NuGet.config','global.json',
        '.gitattributes','backend/.gitattributes','docs/runbooks/release1-deployment.md')
    $dirty = @(& git diff --name-only HEAD -- @inputPaths 2>$null)
    if ($LASTEXITCODE -ne 0 -or $dirty.Count -ne 0) { Stop-Deployment 'verified_build_inputs_required' }
    # Include ignored unknown files; only the SDK's existing bin/obj outputs are
    # excluded. An untracked .cs/config/target must not enter a verified publish.
    $unknown = @(& git -c core.quotepath=false ls-files --others -- @inputPaths 2>$null)
    if ($LASTEXITCODE -ne 0 -or @($unknown | Where-Object { $_ -notmatch '(^|/)(bin|obj)/' }).Count -ne 0) {
        Stop-Deployment 'untracked_build_input_rejected'
    }
    $tree = @(& git -c core.quotepath=false ls-tree -r --full-tree $VerifiedCommit -- @inputPaths 2>$null)
    if ($LASTEXITCODE -ne 0 -or $tree.Count -eq 0) { Stop-Deployment 'verified_input_tree_unavailable' }
    $sourceInputs = @()
    foreach ($line in $tree) {
        if ($line -notmatch '^100(?:644|755) blob ([0-9a-f]{40})\t(.+)$') { Stop-Deployment 'unsupported_build_input' }
        $expectedBlob = $Matches[1]; $relative = $Matches[2]
        $path = Require-DeploymentPath (Join-Path $source $relative.Replace('/','\')) -Existing
        $actualBlob = @(& git hash-object "--path=$relative" -- $path 2>$null)
        if ($LASTEXITCODE -ne 0 -or $actualBlob.Count -ne 1 -or $actualBlob[0] -cne $expectedBlob) { Stop-Deployment 'head_input_blob_mismatch' }
        $sourceInputs += [ordered]@{path=$relative; gitBlob=$expectedBlob; sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash}
    }
    Set-Location -LiteralPath $backend
    $pin = (Read-DeploymentJson (Join-Path $backend 'global.json')).sdk.version
    $sdk = @(& dotnet --version 2>$null)
    if ($LASTEXITCODE -ne 0 -or $sdk.Count -ne 1 -or $sdk[0] -cne $pin) { Stop-Deployment 'pinned_sdk_required' }
    $null = New-Item -ItemType Directory -Path $output
    $bundle = Join-Path $output 'bundle'
    $isolatedArtifacts = Join-Path $output 'build-intermediates'
    $null = New-Item -ItemType Directory -Path $bundle
    $components = @(
        @{Name='api'; Project='src/UnitToko.Api/UnitToko.Api.csproj'},
        @{Name='worker'; Project='src/UnitToko.Worker/UnitToko.Worker.csproj'},
        @{Name='image-decoder'; Project='src/UnitToko.ImageDecoder/UnitToko.ImageDecoder.csproj'}
    )
    foreach ($component in $components) {
        $destination = Join-Path $bundle $component.Name
        $restorePolicy = if ($component.Name -eq 'image-decoder') { @('-p:RestoreLockedMode=true') } else { @() }
        # Capture and discard build text: no environment/configuration body is logged.
        $null = & dotnet publish $component.Project --configuration Release --runtime win-x64 --self-contained false `
            -p:UseAppHost=true -p:ContinuousIntegrationBuild=true -p:SourceRevisionId=$VerifiedCommit `
            --artifacts-path $isolatedArtifacts --output $destination @restorePolicy 2>&1
        if ($LASTEXITCODE -ne 0) { Stop-Deployment 'publish_failed_partial_bundle_retained' }
    }
    $null = New-Item -ItemType Directory -Path (Join-Path $bundle 'deployment')
    Copy-Item -LiteralPath (Join-Path $source 'backend/tools/UnitToko.Deployment') -Destination (Join-Path $bundle 'deployment/tools') -Recurse
    Copy-Item -LiteralPath (Join-Path $source 'docs/runbooks/release1-deployment.md') -Destination (Join-Path $bundle 'deployment/release1-deployment.md')
    $null = New-Item -ItemType Directory -Path (Join-Path $bundle 'native-helpers')
    foreach ($helper in @('local_ocr.py','pdf_preprocess.py')) {
        Copy-Item -LiteralPath (Join-Path $source ('backend/document-processing/' + $helper)) -Destination (Join-Path $bundle 'native-helpers')
    }
    $files = @(Get-ChildItem -LiteralPath $bundle -File -Recurse -Force | Sort-Object FullName | ForEach-Object {
        [ordered]@{path=$_.FullName.Substring($bundle.Length + 1).Replace('\','/'); length=$_.Length;
            sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
    })
    $manifest = [ordered]@{formatVersion=1; sourceCommit=$VerifiedCommit; sdk=$pin; runtime='win-x64'; selfContained=$false;
        releaseProfile=$Profile; acceptance='packaged_only_hosted_acceptance_pending'; sourceInputs=$sourceInputs; files=$files}
    [IO.File]::WriteAllText((Join-Path $bundle 'manifest.json'), ($manifest | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
    $null = Assert-DeploymentBundle $bundle
    Write-Output 'unit_toko_deployment:bundle_verified_hosted_acceptance_pending'
} catch {
    if ($_.Exception.Message -like 'unit_toko_deployment:*') { throw $_.Exception.Message }
    Stop-Deployment 'packaging_failed_partial_bundle_retained'
} finally { Set-Location -LiteralPath $original.Path }
