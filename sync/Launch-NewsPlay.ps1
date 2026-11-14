param([int]$Port = 17921)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$output = Join-Path $root 'artifacts\PCR-Development'
$engine = Join-Path $root 'artifacts\PCR-Development-Engine'
if (!(Test-Path (Join-Path $output 'NewsPlay.exe')) -or !(Test-Path (Join-Path $engine 'NewsPlay.Engine.dll'))) {
    & (Join-Path $PSScriptRoot 'Build-Run.ps1') -BuildOnly -Port $Port
}
$env:NEWSPLAY_DATA = Join-Path $root 'data\PCR-Development'
$env:NEWSPLAY_PORT = $Port.ToString()
$env:NEWSPLAY_DOTNET_HOST = (Get-Command dotnet -ErrorAction Stop).Source
$env:NEWSPLAY_ENGINE_ASSEMBLY = Join-Path $engine 'NewsPlay.Engine.dll'
Start-Process (Join-Path $output 'NewsPlay.exe') -WorkingDirectory $output -WindowStyle Normal
