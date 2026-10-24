param([string]$Executable = 'artifacts/release/ServerTray-win-x64/ServerTray.exe', [int]$Servers = 20, [int]$Seconds = 300, [switch]$Visible, [string]$Receipt = 'artifacts/fixtures/ssh.json')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$exe = [IO.Path]::GetFullPath((Join-Path $projectRoot $Executable))
$receiptPath = [IO.Path]::GetFullPath((Join-Path $projectRoot $Receipt))
if (-not (Test-Path -LiteralPath $exe) -or -not (Test-Path -LiteralPath $receiptPath)) { throw 'Build the portable app and start the SSH fixture first.' }
if ($Servers -lt 1 -or $Servers -gt 20 -or $Seconds -lt 10) { throw 'Expected 1–20 servers and at least 10 seconds.' }
$runName = 'run-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
$dataPath = Join-Path $projectRoot ('artifacts/bench/' + $runName)
$outputPath = $dataPath + '.json'
$arguments = @('--fixture',('"' + $receiptPath + '"'),'--count',[string]$Servers,'--data-dir',('"' + $dataPath + '"'),'--bench-seconds',[string]$Seconds,'--bench-output',('"' + $outputPath + '"'))
if (-not $Visible) { $arguments += '--minimized' }
$process = Start-Process -FilePath $exe -ArgumentList $arguments -WorkingDirectory $projectRoot -WindowStyle Hidden -PassThru
Write-Output ('PID: ' + $process.Id)
Write-Output ('Live report: ' + $outputPath)
