param(
    [string]$DotnetPath,
    [string]$MongoPath,
    [string]$NodePath,
    # release1: the persistent Release 1 pilot. full: a separate demo of every maintained workspace (own database, port and state).
    # review: the loopback-only review stack (https://localhost:8443, MongoDB 27031, tmp/pilot/review) with minted persona sessions.
    [ValidateSet('release1', 'full', 'review')][string]$Profile = 'release1',
    # review only: a folder holding ClamAV signatures already on this machine (main/daily/bytecode .cvd/.cld), copied read-only.
    # Without it the review catalog runs without product photos; the review stack never downloads signatures.
    [string]$ClamavSignatures,
    # Asks the running instance of this profile to stop (as Ctrl+C would) and waits for it to finish.
    [switch]$Stop
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
if ($Stop) {
    $state = Join-Path $repository "tmp/pilot/$Profile"
    $lock = Join-Path $state 'launcher.lock'
    function Test-Running { try { $stream = [IO.File]::Open($lock, 'OpenOrCreate', 'ReadWrite', 'None'); $stream.Dispose(); return $false } catch [IO.IOException] { return $true } }
    if (-not (Test-Path -LiteralPath $state) -or -not (Test-Running)) { Write-Output "The $Profile pilot is not running."; exit 0 }
    Set-Content -LiteralPath (Join-Path $state 'stop-requested') -Value (Get-Date -Format o)
    $deadline = (Get-Date).AddSeconds(90)
    while ((Test-Running) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
    if (Test-Running) { throw "The $Profile pilot did not stop within 90 seconds." }
    Write-Output "The $Profile pilot stopped."
    exit 0
}
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
$release = if ($Profile -eq 'release1') { 'release1' } else { 'full' }
$build = if ($Profile -eq 'release1') { 'frontend/.next/BUILD_ID' } else { 'frontend/.next-full/BUILD_ID' }
if (-not (Test-Path -LiteralPath (Join-Path $repository $build))) { throw "Build the $release frontend first (NEXT_PUBLIC_UNIT_TOKO_RELEASE=$release)." }
$arguments = @('--synthetic-local-pilot', '--repository', $repository, '--mongo', $pilotMongo, '--node', $pilotNode, '--profile', $Profile)
if ($ClamavSignatures) {
    if ($Profile -ne 'review') { throw '-ClamavSignatures is for the review profile; the other profiles update their own signatures.' }
    $arguments += @('--clamav-signatures', (Resolve-Path -LiteralPath $ClamavSignatures).Path)
}
& $pilotDotnet $assembly @arguments
exit $LASTEXITCODE
