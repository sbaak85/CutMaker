$ErrorActionPreference = 'Stop'
$script:CutMakerRoot = Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $script:CutMakerRoot '.tools\dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:NUGET_PACKAGES = Join-Path $script:CutMakerRoot '.cache\nuget'
$env:CUTMAKER_DATA_DIR = Join-Path $script:CutMakerRoot 'runtime'
$env:CUTMAKER_FFMPEG_DIR = Join-Path $script:CutMakerRoot '.tools\ffmpeg\bin'
$localDotnet = Join-Path $script:CutMakerRoot '.tools\dotnet\dotnet.exe'
if (Test-Path -LiteralPath $localDotnet) {
    $script:CutMakerDotnet = $localDotnet
    $env:DOTNET_ROOT = Split-Path -Parent $localDotnet
} else {
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $command) { throw 'Install the SDK using scripts\setup.ps1 first.' }
    $script:CutMakerDotnet = $command.Source
}
Set-Location -LiteralPath $script:CutMakerRoot
