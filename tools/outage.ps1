param([string]$Control = 'artifacts/fixtures/control.json', [int]$AfterSeconds = 60, [int]$OfflineSeconds = 90)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$path = [IO.Path]::GetFullPath((Join-Path $projectRoot $Control))
function Set-Outage([bool]$Offline) {
    $temp = $path + '.scenario.tmp'
    [IO.File]::WriteAllText($temp, ('{"offline":' + $Offline.ToString().ToLowerInvariant() + '}'))
    [IO.File]::Move($temp, $path, $true)
}
Set-Outage $false
try {
    Start-Sleep -Seconds $AfterSeconds
    Set-Outage $true
    Start-Sleep -Seconds $OfflineSeconds
} finally { Set-Outage $false }
