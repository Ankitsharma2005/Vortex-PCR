param([Parameter(Mandatory=$true)][string]$Message)
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
function Git { & git.exe @args; if ($LASTEXITCODE -ne 0) { throw 'Git command failed. Do not switch PCs until push succeeds.' } }
Git add -- .gitignore sync desktop/Directory.Build.props desktop/NewsPlay.PCR.sln desktop/NewsPlay.Core desktop/NewsPlay.Engine desktop/NewsPlay.Desktop desktop/NewsPlay.Tests desktop/NewsPlay.AudioTimingChecks desktop/NewsPlay.ProjectVideoChecks
$changes = Git diff --cached --name-only
if ($changes) { Git commit -m $Message }
Git push -u origin main
Write-Host 'Push successful. On the other PC run sync/Start-Work.ps1 before editing.'
