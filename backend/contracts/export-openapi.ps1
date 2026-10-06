param([switch]$Check, [ValidateSet('Debug','Release')][string]$Configuration = 'Debug', [string]$ArtifactsPath)
$ErrorActionPreference = 'Stop'
$backend = Split-Path $PSScriptRoot -Parent
$repo = Split-Path $backend -Parent
$dotnet = Join-Path $repo '.tools/dotnet10/dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
$contract = Join-Path $PSScriptRoot 'openapi.json'
$previousUri = $env:Mongo__Uri
$previousDatabase = $env:Mongo__Database
$previousEnvironment = $env:ASPNETCORE_ENVIRONMENT
$previousRelease = $env:Release__Profile
$env:Mongo__Uri = 'mongodb://127.0.0.1:27028/?replicaSet=rs0'
$env:Mongo__Database = 'unittoko_openapi_export'
$env:ASPNETCORE_ENVIRONMENT = 'Development'
# Keep the complete future-release contract in source; runtime pilot routing remains restricted.
$env:Release__Profile = 'full'
$process = $null
try {
  $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
  $listener.Start()
  $port = ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
  $listener.Stop()
  $baseUrl = "http://127.0.0.1:$port"
  $apiDll = Join-Path $backend "src/UnitToko.Api/bin/$Configuration/net10.0/UnitToko.Api.dll"
  if ($ArtifactsPath) {
    $artifactRoot = (Resolve-Path -LiteralPath $ArtifactsPath).Path
    $apiDll = Join-Path $artifactRoot ("bin/UnitToko.Api/" + $Configuration.ToLowerInvariant() + "/UnitToko.Api.dll")
  }
  if (-not (Test-Path $apiDll)) { throw 'Build backend/UnitToko.slnx before exporting OpenAPI.' }
  # Start-Process joins ArgumentList elements; preserve the DLL as one argument in paths with spaces.
  $startArgs = @{ FilePath = $dotnet; ArgumentList = @(('"' + $apiDll + '"'),'--urls',$baseUrl); PassThru = $true }
  if ($PSVersionTable.PSEdition -ne 'Core' -or $IsWindows) { $startArgs.WindowStyle = 'Hidden' }
  $process = Start-Process @startArgs
  $body = $null
  $nonce = [Guid]::NewGuid().ToString('N')
  for ($attempt = 0; $attempt -lt 30; $attempt++) {
    if ($process.HasExited) { throw "API exporter process exited with code $($process.ExitCode)." }
    try {
      $response = Invoke-WebRequest "$baseUrl/openapi/v1.json" -Headers @{'X-Correlation-Id'=$nonce} -UseBasicParsing -TimeoutSec 2
      if ($response.Headers['X-Correlation-Id'] -ne $nonce) { throw 'OpenAPI response did not come from the expected API middleware.' }
      $body = $response.Content
      break
    }
    catch { Start-Sleep -Milliseconds 300 }
  }
  if (-not $body) { throw 'OpenAPI endpoint did not become available.' }
  if ($process.HasExited) { throw 'API exporter process exited before export completed.' }
  $document = $body | ConvertFrom-Json
  $document.servers = @()
  $json = $document | ConvertTo-Json -Depth 100
  if ($Check) {
    if (-not (Test-Path $contract) -or ((Get-Content $contract -Raw).Replace("`r`n", "`n").Trim() -ne $json.Replace("`r`n", "`n").Trim())) {
      throw 'OpenAPI contract drift detected. Regenerate backend/contracts/openapi.json.'
    }
  } else {
    [System.IO.File]::WriteAllText($contract, $json + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
  }
} finally {
  if ($process -and -not $process.HasExited) {
    if ($PSVersionTable.PSEdition -eq 'Core') { $process.Kill($true) } else { $process.Kill() }
    $process.WaitForExit(5000) | Out-Null
  }
  $env:Mongo__Uri = $previousUri
  $env:Mongo__Database = $previousDatabase
  $env:ASPNETCORE_ENVIRONMENT = $previousEnvironment
  $env:Release__Profile = $previousRelease
}
