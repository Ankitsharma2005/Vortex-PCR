param(
    [string]$OutputDirectory = [Environment]::GetFolderPath('Desktop'),
    [string]$DataDirectory,
    [int]$Port = 0,
    [switch]$NoDialog
)
$ErrorActionPreference = 'Stop'
# Read-only local collection. Never write the authentication key or media state.
$folders = @($DataDirectory, $env:NEWSPLAY_DATA, "$env:LOCALAPPDATA\NewsPlay",
    "$PSScriptRoot\data\PCR-Development", "$PSScriptRoot\data",
    "$PSScriptRoot\..\data\PCR-Development", "$PSScriptRoot\..\data")
$ports = if ($Port) { @($Port) } else { @(17890,17921) }
$connection = $null
function Find-Engine($keyFiles) {
    foreach ($keyFile in $keyFiles) {
        if (!(Test-Path -LiteralPath $keyFile -PathType Leaf)) { continue }
        $headers = @{ 'X-NewsPlay-Key' = (Get-Content -LiteralPath $keyFile -Raw).Trim() }
        foreach ($candidatePort in $ports) {
            try {
                $base = "http://127.0.0.1:$candidatePort"
                $state = Invoke-RestMethod "$base/state" -Headers $headers -TimeoutSec 3
                return @{ Base=$base; Headers=$headers; State=$state; Port=$candidatePort }
            } catch { } # Do not expose HTTP request details or credentials.
        }
    }
}
$keys = @($folders | Where-Object { $_ } | Select-Object -Unique | ForEach-Object { Join-Path $_ 'engine.key' })
$connection = Find-Engine $keys
if (!$connection -and !$NoDialog) {
    Add-Type -AssemblyName System.Windows.Forms
    $dialog = New-Object System.Windows.Forms.OpenFileDialog
    $dialog.Title = 'Select engine.key from the NewsPlay data folder (Connection / Engine status)'
    $dialog.Filter = 'NewsPlay engine key|engine.key'
    if ($dialog.ShowDialog() -eq 'OK') { $connection = Find-Engine @($dialog.FileName) }
    $dialog.Dispose()
}
function Snapshot {
    if (!$connection) { return @{ Available=$false; Reason='Engine not reached. Start NewsPlay, then run this tool again.' } }
    $timing = $null
    try { $timing = Invoke-RestMethod ($connection.Base + '/audio-sync') -Headers $connection.Headers -TimeoutSec 3 }
    catch { $timing = @{ Available=$false; Reason='Audio timing endpoint unavailable in this build or engine disconnected.' } }
    $routes = @()
    try {
        $state = Invoke-RestMethod ($connection.Base + '/state') -Headers $connection.Headers -TimeoutSec 3
        $routes = @($state.channels | Select-Object id,status,audioDeviceId,audioSampleRate,audioBitDepth,gainDb,mute,autoLevel)
    } catch { }
    return @{ At=[DateTimeOffset]::Now.ToString('o'); Routes=$routes; Timing=$timing }
}
Write-Host 'Keep the affected clip playing. Collecting two audio snapshots over 8 seconds...'
$first = Snapshot
Start-Sleep -Seconds 8
$second = Snapshot
$processes = @(Get-Process -Name 'NewsPlay','NewsPlay.Engine' -ErrorAction SilentlyContinue | ForEach-Object {
    try {
        $exe = $_.Path
        $dll = Join-Path (Split-Path $exe) ($_.ProcessName + '.dll')
        [pscustomobject]@{ Name=$_.ProcessName; Version=$_.FileVersion; Binary=Split-Path $exe -Leaf;
            DllSha256=if (Test-Path -LiteralPath $dll) { (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash } else { $null } }
    } catch { }
})
$report = [ordered]@{ Schema=1; At=[DateTimeOffset]::Now.ToString('o'); EnginePort=if ($connection) { $connection.Port } else { $null };
    Processes=$processes; Snapshots=@($first,$second);
    Notes='Read-only local collection. No audio, media URLs or authentication keys. Starved frames can include end-of-clip silence; they are not a hardware underrun measurement.' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$output = Join-Path $OutputDirectory ('NewsPlay-audio-diagnostics-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.json')
$report | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $output -Encoding UTF8
Write-Host "Saved: $output"
if (!$connection) { Write-Warning 'Engine was not reached. Report contains limited information; start NewsPlay and collect again.' }
