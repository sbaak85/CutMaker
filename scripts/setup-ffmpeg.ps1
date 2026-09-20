$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$cutmakerRoot = Split-Path -Parent $PSScriptRoot
$toolRoot = Join-Path $cutmakerRoot '.tools'
$destination = Join-Path $toolRoot 'ffmpeg'
$version = '9.0.2'
$expectedHash = '60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba'
if (Test-Path -LiteralPath (Join-Path $destination 'bin\ffmpeg.exe')) {
    & (Join-Path $destination 'bin\ffmpeg.exe') -version | Select-Object -First 1
    Write-Output "Using project-local FFmpeg: $destination"
    exit 0
}
$downloads = Join-Path $toolRoot 'downloads'
New-Item -ItemType Directory -Force -Path $downloads | Out-Null
$archive = Join-Path $downloads "ffmpeg-$version.zip"
# Fixed release + recorded SHA-256; downloaded tools and binaries are never committed.
$url = "https://github.com/GyanD/codexffmpeg/releases/download/$version/ffmpeg-$version-essentials_build.zip"
Invoke-WebRequest -Uri $url -OutFile $archive
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expectedHash) {
    throw 'FFmpeg checksum mismatch. Installation stopped; do not run the downloaded archive.'
}
$unpacked = Join-Path $toolRoot "ffmpeg-$version-unpacked"
Expand-Archive -LiteralPath $archive -DestinationPath $unpacked -Force
$package = Get-ChildItem -LiteralPath $unpacked -Directory | Select-Object -First 1
New-Item -ItemType Directory -Force -Path $destination | Out-Null
Copy-Item -LiteralPath (Join-Path $package.FullName 'bin') -Destination $destination -Recurse -Force
Get-ChildItem -LiteralPath $package.FullName -File | Copy-Item -Destination $destination -Force
& (Join-Path $destination 'bin\ffmpeg.exe') -version | Select-Object -First 1
Write-Output "Installed FFmpeg locally: $destination (no system PATH or codec pack changes)."
