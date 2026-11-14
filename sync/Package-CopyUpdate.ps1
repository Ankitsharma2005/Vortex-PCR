param([string]$PackageName = 'NewsPlay-PCR-Update-2026-11-14')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$package = Join-Path $root ('artifacts\updates\' + $PackageName)
$app = Join-Path $package 'App'
if (!(Test-Path (Join-Path $app 'NewsPlay.exe')) -or !(Test-Path (Join-Path $app 'engine\NewsPlay.Engine.exe'))) { throw 'Publish the self-contained desktop and engine into the package App folder first.' }
New-Item -ItemType Directory -Path (Join-Path $app 'engine\ffmpeg') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'tools\ffmpeg\ffmpeg.exe') -Destination (Join-Path $app 'engine\ffmpeg\ffmpeg.exe') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install-CopyUpdate.ps1') -Destination (Join-Path $package 'Install-Update.ps1') -Force
$ffmpegLicense = Get-ChildItem -LiteralPath (Join-Path $root 'tools\ffmpeg\release') -File -Recurse -Filter 'LICENSE' -ErrorAction SilentlyContinue | Select-Object -First 1
if ($ffmpegLicense) { Copy-Item -LiteralPath $ffmpegLicense.FullName -Destination (Join-Path $package 'FFmpeg-LICENSE.txt') -Force }
@'
@echo off
cd /d "%~dp0"
powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File "%~dp0Install-Update.ps1"
if errorlevel 1 echo Update failed. If the install folder is protected, run this file as Administrator.
pause
'@ | Set-Content -LiteralPath (Join-Path $package 'UPDATE-NEWSPLAY.cmd') -Encoding Ascii
@'
NEWSPLAY PCR - COPY UPDATE - 14 NOVEMBER 2026

1. Copy this complete ZIP to the other Windows x64 PC and extract it.
2. Stop playout/streams. Shut down the NewsPlay engine from the app, then close NewsPlay.
3. Double-click UPDATE-NEWSPLAY.cmd.
4. Select the OLD installed NewsPlay folder containing NewsPlay.exe.
5. The updater verifies files, backs up replaced application files, and installs the update.
6. Start NewsPlay from the updated Desktop shortcut or the old installation folder.

If installed in Program Files, right-click UPDATE-NEWSPLAY.cmd and Run as administrator.
Do not uninstall the old app. Do not copy/delete your data folder.
This package contains application files only. It does not include this development PC's database, playlists, media, credentials or activation state.
Existing saved data and custom CG templates are not overwritten. A backup of replaced application files is kept beside the installation folder.

Manual alternative: after closing both app and engine, copy the CONTENTS of App into the old installation folder and replace application files. Keep your data folders and customized CG templates.

Includes the .NET runtime, playback engine, native VLC libraries and FFmpeg. No .NET SDK/build commands are needed.
Microsoft WebView2 Runtime is required for browser/CG features. Blackmagic Desktop Video is required for DeckLink, and the GPU driver is required for GPU decoding.
GPU decoding is independent of CPU scaling. Hardware output/lip-sync and real-time UHD performance still require validation on that PC.
'@ | Set-Content -LiteralPath (Join-Path $package 'READ-ME-FIRST.txt') -Encoding UTF8
$files = @(Get-ChildItem -LiteralPath $app -File -Recurse | ForEach-Object { [pscustomobject]@{path=$_.FullName.Substring($app.Length+1);sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash} })
@{ build=$PackageName; files=$files } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $package 'package-manifest.json') -Encoding UTF8
$temporaryZip = $package + '-building.zip'
Compress-Archive -LiteralPath $package -DestinationPath $temporaryZip -CompressionLevel Optimal -Force
Move-Item -LiteralPath $temporaryZip -Destination ($package + '.zip') -Force
Get-Item -LiteralPath ($package + '.zip') | Select-Object FullName,Length
