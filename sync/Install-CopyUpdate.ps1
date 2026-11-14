param([string]$InstallDirectory, [switch]$SkipShortcut)
$ErrorActionPreference = 'Stop'
$packageRoot = Split-Path $MyInvocation.MyCommand.Path -Parent
$appRoot = Join-Path $packageRoot 'App'
if (!(Test-Path -LiteralPath (Join-Path $appRoot 'NewsPlay.exe'))) { throw 'Extract the complete update ZIP before running this script.' }
if (!$InstallDirectory) {
    Add-Type -AssemblyName System.Windows.Forms
    $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
    $dialog.Description = 'Select the OLD installed NewsPlay folder containing NewsPlay.exe'
    $dialog.ShowNewFolderButton = $false
    if ($dialog.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) { exit }
    $InstallDirectory = $dialog.SelectedPath
}
$targetRoot = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
if (!(Test-Path -LiteralPath (Join-Path $targetRoot 'NewsPlay.exe'))) { throw 'Choose the existing installation folder that contains NewsPlay.exe. No files were changed.' }
$prefix = $targetRoot + '\'
if ($targetRoot -eq [IO.Path]::GetFullPath($appRoot).TrimEnd('\')) { throw 'Select the OLD installation, not the App folder in this update.' }
$running = @(Get-CimInstance Win32_Process | Where-Object {
    ($_.ExecutablePath -and $_.ExecutablePath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) -or
    ($_.Name -eq 'dotnet.exe' -and $_.CommandLine -and $_.CommandLine.IndexOf((Join-Path $targetRoot 'engine\'), [StringComparison]::OrdinalIgnoreCase) -ge 0)
})
if ($running.Count -gt 0) { throw 'NewsPlay or its engine is still running from that folder. Stop playback, shut down the engine from NewsPlay, close the app, then run the update again. No files were changed.' }
$manifest = Get-Content -LiteralPath (Join-Path $packageRoot 'package-manifest.json') -Raw | ConvertFrom-Json
$copyFiles = @()
foreach ($entry in $manifest.files) {
    $source = [IO.Path]::GetFullPath((Join-Path $appRoot $entry.path))
    $target = [IO.Path]::GetFullPath((Join-Path $targetRoot $entry.path))
    if (!$source.StartsWith(($appRoot + '\'), [StringComparison]::OrdinalIgnoreCase) -or !$target.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid package path. No files were changed.' }
    if (!(Test-Path -LiteralPath $source) -or (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $entry.sha256) { throw ('Package file missing/damaged: ' + $entry.path) }
    # Preserve templates already customized in the installed application.
    if ($entry.path.StartsWith('Cg\', [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $target)) { continue }
    $copyFiles += [pscustomobject]@{ Relative = $entry.path; Source = $source; Target = $target; Existed = (Test-Path -LiteralPath $target) }
}
$backupRoot = Join-Path (Split-Path $targetRoot -Parent) ((Split-Path $targetRoot -Leaf) + '-backup-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $backupRoot -ErrorAction Stop | Out-Null
# Finish backing up every replaced file before overwriting any application file.
foreach ($file in $copyFiles | Where-Object Existed) {
    $backup = Join-Path $backupRoot $file.Relative
    New-Item -ItemType Directory -Path (Split-Path $backup -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $file.Target -Destination $backup -Force
}
try {
    foreach ($file in $copyFiles) {
        New-Item -ItemType Directory -Path (Split-Path $file.Target -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $file.Source -Destination $file.Target -Force
    }
} catch {
    foreach ($file in $copyFiles | Where-Object Existed) { Copy-Item -LiteralPath (Join-Path $backupRoot $file.Relative) -Destination $file.Target -Force }
    throw ('Update did not complete; original application files were restored. Backup: ' + $backupRoot + '. ' + $_.Exception.Message)
}
if (!$SkipShortcut) {
    $shell = New-Object -ComObject WScript.Shell
    $shortcutPath = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'NewsPlay PCR.lnk'
    $existingShortcut = Test-Path -LiteralPath $shortcutPath
    $shortcut = $shell.CreateShortcut($shortcutPath)
    # Keep an existing launcher/arguments that may select a custom data directory.
    if (!$existingShortcut) {
        $shortcut.TargetPath = Join-Path $targetRoot 'NewsPlay.exe'
        $shortcut.WorkingDirectory = $targetRoot
    }
    $shortcut.IconLocation = (Join-Path $targetRoot 'NewsPlay.exe') + ',0'
    $shortcut.Description = 'NewsPlay PCR Automation'
    $shortcut.Save()
}
Write-Host 'UPDATE COMPLETE' -ForegroundColor Green
Write-Host ('Open: ' + (Join-Path $targetRoot 'NewsPlay.exe'))
Write-Host ('Previous application backup: ' + $backupRoot)
Write-Host 'Existing databases, playlists, media, activation files and custom CG templates were not overwritten.'
