param([string]$RemoteUrl)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
function Git { & git.exe @args; if ($LASTEXITCODE -ne 0) { throw 'Git command failed. Resolve the reported error before continuing.' } }
if (!(Test-Path (Join-Path $root '.git'))) { Git init -b main }
if ($RemoteUrl) {
    $current = & git.exe remote get-url origin 2>$null
    if ($LASTEXITCODE -eq 0) { if ($current -ne $RemoteUrl) { throw 'Existing origin differs; review before changing it.' } }
    else { Git remote add origin $RemoteUrl }
}
$changes = Git status --porcelain
if ($changes) { throw 'Local changes exist. Finish/save them before pulling work from the other PC.' }
Git pull --ff-only origin main
Write-Host 'Latest source ready. Open desktop/NewsPlay.PCR.sln.'
