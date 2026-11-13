param([switch]$BuildOnly, [int]$Port=17921)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
$env:NEWSPLAY_DATA = Join-Path $root 'data\PCR-Development'
$env:NEWSPLAY_PORT = $Port.ToString()
$env:NEWSPLAY_DOTNET_HOST = $dotnet
$output = Join-Path $root 'artifacts\PCR-Development'
# Separate output directories avoid desktop build cleanup removing engine dependencies.
$engine = Join-Path $root 'artifacts\PCR-Development-Engine'
& $dotnet build desktop/NewsPlay.Desktop/NewsPlay.Desktop.csproj -c Debug -o $output
if ($LASTEXITCODE -ne 0) { throw 'Desktop build failed' }
& $dotnet build desktop/NewsPlay.Engine/NewsPlay.Engine.csproj -c Debug -o $engine
if ($LASTEXITCODE -ne 0) { throw 'Engine build failed' }
$env:NEWSPLAY_ENGINE_ASSEMBLY = Join-Path $engine 'NewsPlay.Engine.dll'
if (!(Test-Path (Join-Path $engine 'libvlc\win-x64\libvlc.dll'))) { throw 'Native VLC runtime missing; restore NuGet packages before launching.' }
$encoder = Join-Path $root 'tools\ffmpeg\ffmpeg.exe'
if (Test-Path $encoder) { New-Item (Join-Path $engine 'ffmpeg') -ItemType Directory -Force | Out-Null; Copy-Item $encoder (Join-Path $engine 'ffmpeg\ffmpeg.exe') -Force }
else { Write-Warning 'Streaming needs a matching static FFmpeg build at tools/ffmpeg/ffmpeg.exe; playback can run without it.' }
if (!$BuildOnly) { Start-Process (Join-Path $output 'NewsPlay.exe') -WorkingDirectory $output -WindowStyle Normal }
