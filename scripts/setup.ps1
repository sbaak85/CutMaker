$ErrorActionPreference = 'Stop'
$cutmakerRoot = Split-Path -Parent $PSScriptRoot
$sdkVersion = (Get-Content -LiteralPath (Join-Path $cutmakerRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version
$toolDirectory = Join-Path (Split-Path -Parent $cutmakerRoot) '.tools'
$sdkDirectory = Join-Path $toolDirectory 'dotnet10'
if (Test-Path -LiteralPath (Join-Path $sdkDirectory "sdk\$sdkVersion")) {
    Write-Output "CutMaker SDK $sdkVersion is already available."
    exit 0
}
New-Item -ItemType Directory -Force -Path $toolDirectory | Out-Null
$installer = Join-Path $toolDirectory 'dotnet-install.ps1'
Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer
& $installer -Version $sdkVersion -InstallDir $sdkDirectory -NoPath
if (-not (Test-Path -LiteralPath (Join-Path $sdkDirectory "sdk\$sdkVersion\dotnet.dll"))) { throw "SDK installation incomplete: $sdkVersion" }
Write-Output "Installed Microsoft .NET SDK $sdkVersion locally: $sdkDirectory"
