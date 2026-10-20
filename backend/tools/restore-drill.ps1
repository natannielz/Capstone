# Q09 timed restore drill on a synthetic database (docs/runbooks/restore-drill.md).
# Starts a throwaway local mongod, applies the full schema, seeds synthetic rows and evidence objects, captures a
# manifest, stops cleanly, copies the files (backup), copies them again (restore), starts a node on the restored copy
# and verifies every collection and evidence object. Never touches the development database on 27028 or real data.
param(
    [Parameter(Mandatory)][string]$MongodPath,
    [string]$DotnetPath = './.tools/dotnet10/dotnet.exe',
    [string]$Work = "tmp/restore-drill/$(Get-Date -Format 'yyyyMMdd-HHmmss')",
    [int]$Port = 27091,
    [int]$Documents = 20000,
    [int]$EvidenceObjects = 200,
    [string]$ArtifactsPath = 'tmp/restore-drill/artifacts'
)
$ErrorActionPreference = 'Stop'
Set-Location (Resolve-Path "$PSScriptRoot/../..")
& $DotnetPath build backend/tools/UnitToko.RestoreDrill/UnitToko.RestoreDrill.csproj --configuration Release --artifacts-path $ArtifactsPath --nologo -v:q
if ($LASTEXITCODE -ne 0) { throw 'The restore drill tool did not build.' }
$tool = Join-Path $ArtifactsPath 'bin/UnitToko.RestoreDrill/release/UnitToko.RestoreDrill.dll'
& $DotnetPath $tool drill --mongod (Resolve-Path $MongodPath).Path --work $Work --port $Port --documents $Documents --evidence-objects $EvidenceObjects
exit $LASTEXITCODE
