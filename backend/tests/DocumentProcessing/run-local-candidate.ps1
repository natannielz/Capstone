param(
    [Parameter(Mandatory)][string]$Python,
    [Parameter(Mandatory)][string]$Packages,
    [Parameter(Mandatory)][string]$Pillow,
    [Parameter(Mandatory)][string]$Models,
    [Parameter(Mandatory)][string]$Font,
    [Parameter(Mandatory)][string]$Dotnet
)
$ErrorActionPreference = 'Stop'
$ocrRepository = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
$ocrFixtures = Join-Path $ocrRepository 'tmp/production/ocr-fixtures'
$ocrResults = Join-Path $ocrRepository 'tmp/production/ocr-results'
# Caller supplies existing local dependencies. This command does not install/download models,
# register a processor, change production policies or contact an external OCR service.
foreach ($ocrPath in @($Python, $Packages, $Pillow, $Models, $Font, $Dotnet)) {
    if (-not [System.IO.Path]::IsPathFullyQualified($ocrPath) -or -not (Test-Path -LiteralPath $ocrPath)) { throw 'Explicit existing absolute candidate paths are required' }
}
$ocrVariables = @{
    UNITTOKO_OCR_PYTHON = $Python
    UNITTOKO_OCR_PACKAGES = $Packages
    UNITTOKO_OCR_PILLOW = $Pillow
    UNITTOKO_OCR_MODELS = $Models
    UNITTOKO_OCR_HELPER = Join-Path $ocrRepository 'backend/document-processing/local_ocr.py'
    UNITTOKO_OCR_FIXTURES = $ocrFixtures
    UNITTOKO_DECODER_DOTNET = $Dotnet
}
$ocrPrevious = @{}
try {
    foreach ($ocrName in $ocrVariables.Keys) {
        $ocrPrevious[$ocrName] = [Environment]::GetEnvironmentVariable($ocrName, 'Process')
        [Environment]::SetEnvironmentVariable($ocrName, $ocrVariables[$ocrName], 'Process')
    }
    & $Python -B (Join-Path $PSScriptRoot 'generate-synthetic.py') --output $ocrFixtures --font $Font
    if ($LASTEXITCODE -ne 0) { throw 'Synthetic fixture generation failed' }
    & $Dotnet build (Join-Path $ocrRepository 'backend/tests/UnitToko.Tests/UnitToko.Tests.csproj') --no-restore --nologo
    if ($LASTEXITCODE -ne 0) { throw 'OCR candidate build failed' }
    $ocrReportName = 'ocr-candidate-' + [DateTime]::UtcNow.ToString('yyyyMMddHHmmss') + '.trx'
    & $Dotnet test (Join-Path $ocrRepository 'backend/tests/UnitToko.Tests/UnitToko.Tests.csproj') --no-build --no-restore `
        --filter 'FullyQualifiedName~PaymentExtractionTests|FullyQualifiedName~LocalOcrTests|FullyQualifiedName~LocalOcrBenchmarkTests|FullyQualifiedName~ImageDecoderTests|FullyQualifiedName~ImageDecoderAvailabilityTests' `
        --logger ('trx;LogFileName=' + $ocrReportName) --results-directory $ocrResults
    if ($LASTEXITCODE -ne 0) { throw 'OCR candidate checks failed; inspect retained TRX and any measured report' }
    [xml]$ocrTrx = Get-Content -LiteralPath (Join-Path $ocrResults $ocrReportName) -Raw
    $ocrCounters = $ocrTrx.TestRun.ResultSummary.Counters
    if ([int]$ocrCounters.total -ne 31 -or [int]$ocrCounters.passed -ne 31 -or [int]$ocrCounters.notExecuted -ne 0) { throw 'Missing, failing or skipped OCR/process checks' }
    Write-Output 'Local synthetic candidate checks passed. Read the measurements; this does not select a production model.'
}
finally {
    foreach ($ocrName in $ocrPrevious.Keys) { [Environment]::SetEnvironmentVariable($ocrName, $ocrPrevious[$ocrName], 'Process') }
}
