param([string]$DotNet = 'dotnet', [switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$projectRoot = Split-Path $PSScriptRoot -Parent
Push-Location $projectRoot
try {
    & $DotNet restore ServerTray.sln --locked-mode
    if ($LASTEXITCODE) { throw 'Locked restore failed' }
    if (-not $SkipTests) {
        & $DotNet test ServerTray.sln -c Release --no-restore --logger 'trx'
        if ($LASTEXITCODE) { throw 'Tests failed' }
    }
    python tools/scan_public.py --history
    if ($LASTEXITCODE) { throw 'Source scan failed' }
    $publish = Join-Path $projectRoot 'artifacts/release/ServerTray-win-x64'
    $resolvedPublish = [IO.Path]::GetFullPath($publish)
    $expectedRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts/release')) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedPublish.StartsWith($expectedRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Publish directory escapes the generated release folder' }
    if (Test-Path -LiteralPath $resolvedPublish) { Remove-Item -LiteralPath $resolvedPublish -Recurse -Force }
    & $DotNet publish src/ServerTray.App -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -p:DebugType=None -p:DebugSymbols=false -o $publish
    if ($LASTEXITCODE) { throw 'Publish failed' }
    python tools/collect_notices.py --publish $publish
    if ($LASTEXITCODE) { throw 'Dependency notices failed' }
    Copy-Item -LiteralPath 'LICENSE','README.md','README.zh-CN.md','SECURITY.md','CONTRIBUTING.md','CHANGELOG.md' -Destination $publish -Force
    Copy-Item -LiteralPath 'docs','examples' -Destination $publish -Recurse -Force
    $unsafeFiles = Get-ChildItem -LiteralPath $publish -Recurse -File | Where-Object { $_.Name -in @('config.json','credentials.json','known-hosts.json','alert-state.json') -or $_.Extension -in @('.db','.sqlite','.pem','.key','.pfx','.log') }
    if ($unsafeFiles) { throw 'Private-data files detected in publish directory' }
    $archive = Join-Path $projectRoot 'artifacts/release/ServerTray-0.1.0-preview-win-x64.zip'
    Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $archive -Force -CompressionLevel Optimal
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath ($archive + '.sha256') -Value ($hash + '  ' + [IO.Path]::GetFileName($archive)) -Encoding utf8
    Write-Output $archive
} finally { Pop-Location }
