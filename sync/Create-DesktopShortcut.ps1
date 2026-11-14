$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$desktop = [Environment]::GetFolderPath('DesktopDirectory')
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut((Join-Path $desktop 'NewsPlay PCR.lnk'))
$shortcut.TargetPath = Join-Path $PSHOME 'powershell.exe'
$shortcut.Arguments = '-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "' + (Join-Path $PSScriptRoot 'Launch-NewsPlay.ps1') + '"'
$shortcut.WorkingDirectory = $root
$shortcut.IconLocation = (Join-Path $root 'desktop\NewsPlay.Desktop\Assets\NewsPlay.ico') + ',0'
$shortcut.Description = 'NewsPlay PCR Automation'
$shortcut.Save()
Write-Output (Join-Path $desktop 'NewsPlay PCR.lnk')
