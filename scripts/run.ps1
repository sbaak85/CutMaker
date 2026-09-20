. (Join-Path $PSScriptRoot 'environment.ps1')
& $script:CutMakerDotnet run --project 'src\CutMaker.App\CutMaker.App.csproj' --configuration Release
if ($LASTEXITCODE -ne 0) { throw "CutMaker exited with code $LASTEXITCODE" }
