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
$ocrSupplierFixtures = Join-Path $ocrRepository 'tmp/production/supplier-ocr-fixtures'
$ocrResults = Join-Path $ocrRepository 'tmp/production/ocr-results'
# Caller supplies existing local dependencies. This command does not install/download models,
# register a processor, change production policies or contact an external OCR service.
foreach ($ocrPath in @($Python, $Packages, $Pillow, $Models, $Font, $Dotnet)) {
    if (-not [System.IO.Path]::IsPathFullyQualified($ocrPath) -or -not (Test-Path -LiteralPath $ocrPath)) { throw 'Explicit existing absolute OCR dependency paths are required' }
}
$ocrVariables = @{
    UNITTOKO_OCR_PYTHON = $Python
    UNITTOKO_OCR_PACKAGES = $Packages
    UNITTOKO_OCR_PILLOW = $Pillow
    UNITTOKO_OCR_MODELS = $Models
    UNITTOKO_OCR_HELPER = Join-Path $ocrRepository 'backend/document-processing/local_ocr.py'
    UNITTOKO_OCR_FIXTURES = $ocrFixtures
    UNITTOKO_SUPPLIER_OCR_FIXTURES = $ocrSupplierFixtures
    UNITTOKO_DECODER_DOTNET = $Dotnet
}
$ocrPrevious = @{}
try {
    foreach ($ocrName in $ocrVariables.Keys) {
        $ocrPrevious[$ocrName] = [Environment]::GetEnvironmentVariable($ocrName, 'Process')
        [Environment]::SetEnvironmentVariable($ocrName, [IO.Path]::GetFullPath($ocrVariables[$ocrName]), 'Process')
    }
    & $Python -B (Join-Path $PSScriptRoot 'generate-synthetic.py') --output $ocrFixtures --font $Font
    if ($LASTEXITCODE -ne 0) { throw 'Synthetic fixture generation failed' }
    & $Python -B (Join-Path $PSScriptRoot 'generate-synthetic.py') --supplier-only --output $ocrSupplierFixtures --font $Font
    if ($LASTEXITCODE -ne 0) { throw 'Synthetic supplier fixture generation failed' }
    & $Dotnet build (Join-Path $ocrRepository 'backend/tests/UnitToko.Tests/UnitToko.Tests.csproj') --no-restore --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Local OCR build failed' }
    $ocrReportName = 'ocr-candidate-' + [DateTime]::UtcNow.ToString('yyyyMMddHHmmss') + '.trx'
    $ocrRequiredSuites = @('PaymentExtractionTests', 'LocalOcrTests', 'LocalOcrBenchmarkTests', 'SupplierDocumentLocalOcrTests', 'ImageDecoderTests', 'ImageDecoderAvailabilityTests')
    $ocrFilter = ($ocrRequiredSuites | ForEach-Object { 'FullyQualifiedName~.' + $_ + '.' }) -join '|'
    & $Dotnet test (Join-Path $ocrRepository 'backend/tests/UnitToko.Tests/UnitToko.Tests.csproj') --no-build --no-restore `
        --filter $ocrFilter `
        --logger ('trx;LogFileName=' + $ocrReportName) --results-directory $ocrResults
    if ($LASTEXITCODE -ne 0) { throw 'Local OCR checks failed; inspect retained TRX and measured reports' }
    [xml]$ocrTrx = Get-Content -LiteralPath (Join-Path $ocrResults $ocrReportName) -Raw
    $ocrCounters = $ocrTrx.TestRun.ResultSummary.Counters
    $ocrRows = @($ocrTrx.TestRun.Results.UnitTestResult)
    if ([int]$ocrCounters.total -lt 1 -or [int]$ocrCounters.passed -ne [int]$ocrCounters.total -or
        [int]$ocrCounters.notExecuted -ne 0 -or $ocrRows.Count -ne [int]$ocrCounters.total -or
        @($ocrRows | Where-Object { $_.outcome -ne 'Passed' }).Count -ne 0) {
        throw 'Missing, failing or skipped OCR/process checks'
    }
    foreach ($ocrSuite in $ocrRequiredSuites) {
        if (-not ($ocrRows | Where-Object { $_.testName -match ('(?:^|\.)' + [regex]::Escape($ocrSuite) + '\.') })) {
            throw "Missing required OCR/process suite: $ocrSuite"
        }
    }
    Write-Output 'Selected local OCR engine checks passed on synthetic documents. Read the measurements; host/policy approval and real-document acceptance remain required.'
}
finally {
    foreach ($ocrName in $ocrPrevious.Keys) { [Environment]::SetEnvironmentVariable($ocrName, $ocrPrevious[$ocrName], 'Process') }
}
