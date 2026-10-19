param(
    [string]$DotnetPath,
    [string]$MongoPath,
    [string]$NodePath,
    # release1: the persistent Release 1 pilot. full: a separate demo of every maintained workspace (own database, port and state).
    [ValidateSet('release1', 'full')][string]$Profile = 'release1'
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
function Existing-Tool([string]$Provided, [string]$Name, [string]$Pattern) {
    if ($Provided) {
        $resolved = (Resolve-Path -LiteralPath $Provided).Path
        if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) { throw "Missing $Name executable." }
        return $resolved
    }
    $installed = Get-Command $Name -ErrorAction SilentlyContinue
    if ($installed) { return $installed.Source }
    $local = Get-ChildItem -LiteralPath (Join-Path $repository '.tools') -Filter $Pattern -Recurse -File -ErrorAction SilentlyContinue | Sort-Object FullName | Select-Object -First 1
    if ($local) { return $local.FullName }
    throw "Supply the already inspected $Name executable path. No installation is performed."
}
$pilotDotnet = Existing-Tool $DotnetPath 'dotnet' 'dotnet.exe'
$pilotMongo = Existing-Tool $MongoPath 'mongod' 'mongod.exe'
$pilotNode = Existing-Tool $NodePath 'node' 'node.exe'
$assembly = Join-Path $PSScriptRoot 'bin/Release/net10.0/UnitToko.LocalPilot.dll'
if (-not (Test-Path -LiteralPath $assembly)) { throw 'Build backend/tools/UnitToko.LocalPilot in Release first; this launcher does not build.' }
$build = if ($Profile -eq 'full') { 'frontend/.next-full/BUILD_ID' } else { 'frontend/.next/BUILD_ID' }
if (-not (Test-Path -LiteralPath (Join-Path $repository $build))) { throw "Build the $Profile frontend first (NEXT_PUBLIC_UNIT_TOKO_RELEASE=$Profile)." }
& $pilotDotnet $assembly --synthetic-local-pilot --repository $repository --mongo $pilotMongo --node $pilotNode --profile $Profile
exit $LASTEXITCODE
