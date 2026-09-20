param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')
. (Join-Path $PSScriptRoot 'environment.ps1')
& $script:CutMakerDotnet build 'CutMaker.slnx' --configuration $Configuration
if ($LASTEXITCODE -ne 0) { throw "Build failed: $LASTEXITCODE" }
