. (Join-Path $PSScriptRoot 'environment.ps1')
& $script:CutMakerDotnet build 'CutMaker.slnx' --configuration Release
if ($LASTEXITCODE -ne 0) { throw "Build failed: $LASTEXITCODE" }
& $script:CutMakerDotnet run --project 'tests\CutMaker.Core.Checks\CutMaker.Core.Checks.csproj' --configuration Release --no-build
if ($LASTEXITCODE -ne 0) { throw "Core checks failed: $LASTEXITCODE" }
$env:CUTMAKER_DATA_DIR = Join-Path $script:CutMakerRoot 'runtime\verification'
$app = Join-Path $script:CutMakerRoot 'src\CutMaker.App\bin\Release\net10.0-windows\CutMaker.exe'
$process = Start-Process -FilePath $app -ArgumentList '--smoke-test' -WindowStyle Hidden -PassThru
if (-not $process.WaitForExit(180000)) {
    $process.Kill()
    throw 'Application smoke check timed out.'
}
$process.Refresh()
$smokeResult = Get-Content -LiteralPath (Join-Path $env:CUTMAKER_DATA_DIR 'smoke\result.txt') -Raw
Write-Output $smokeResult
if ($process.ExitCode -ne 0 -or -not $smokeResult.StartsWith('PASS:')) { throw "Application smoke check failed: $($process.ExitCode)" }
Get-Content -LiteralPath (Join-Path $env:CUTMAKER_DATA_DIR 'smoke\import-result.txt')
Get-Content -LiteralPath (Join-Path $env:CUTMAKER_DATA_DIR 'smoke\timeline-result.txt')
Get-Content -LiteralPath (Join-Path $env:CUTMAKER_DATA_DIR 'smoke\editing-result.txt')
Get-Content -LiteralPath (Join-Path $env:CUTMAKER_DATA_DIR 'smoke\batch-result.txt')
Get-Content -LiteralPath (Join-Path $env:CUTMAKER_DATA_DIR 'smoke\convenience-result.txt')
Get-Content -LiteralPath (Join-Path $env:CUTMAKER_DATA_DIR 'smoke\navigation-result.txt')
Get-Content -LiteralPath (Join-Path $env:CUTMAKER_DATA_DIR 'smoke\zoom-range-result.txt')
Get-Content -LiteralPath (Join-Path $env:CUTMAKER_DATA_DIR 'smoke\track-controls-result.txt')
Get-Content -LiteralPath (Join-Path $env:CUTMAKER_DATA_DIR 'smoke\transport-result.txt')
Get-Content -LiteralPath (Join-Path $env:CUTMAKER_DATA_DIR 'smoke\ime-hotkey-result.txt')
Get-Content -LiteralPath (Join-Path $env:CUTMAKER_DATA_DIR 'smoke\recovery-result.txt')
Get-Content -LiteralPath (Join-Path $env:CUTMAKER_DATA_DIR 'smoke\preview-result.txt')
Get-Content -LiteralPath (Join-Path $env:CUTMAKER_DATA_DIR 'smoke\preview-cache-result.txt')
& $script:CutMakerDotnet run --project 'tests\CutMaker.Render.Checks\CutMaker.Render.Checks.csproj' --configuration Release --no-build
if ($LASTEXITCODE -ne 0) { throw "Render checks failed: $LASTEXITCODE" }
