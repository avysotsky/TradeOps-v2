param(
    [ValidateSet('b2', 'b3', 'b3-stream', 'b3-host', 'b3-session', 'b3-inbox', 'b3-testnet-order', 'b3-mainnet-eth', 'b4-order', 'b4-order-reducer', 'b4-reader', 'b4-binding', 'b4-collector', 'b4-known-orders', 'b4-gate', 'b4-replay', 'b4-replay-gate', 'b4-account', 'b4-account-host', 'b4-account-manifest', 'b4-account-assessment', 'b4-unified', 'fixture')][string]$Suite = 'b2',
    [string]$Dotnet = 'D:\DotNet\dotnet.exe'
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output = Join-Path $repo '.tradeops/postgres-reuse'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$previous = $env:DOTNET_CLI_USE_MSBUILD_SERVER
try {
    $env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'
    $log = Join-Path $output ('build-' + (Get-Date -Format 'yyyyMMddTHHmmss') + '.log')
    & $Dotnet build (Join-Path $repo 'tests/TradeOps.UnitTests/TradeOps.UnitTests.csproj') `
        -c Release --no-restore -m:1 -nr:false -p:BuildInParallel=false -p:UseSharedCompilation=false *> $log
    $buildResult = $LASTEXITCODE
    Get-Content -LiteralPath $log -Tail 20
    if ($buildResult -ne 0) { throw "Build failed: $log" }
    if ($Suite -in @('b4-account-host','b4-account-manifest')) { & (Join-Path $PSScriptRoot 'Test-AccountSnapshotDiagnostics.ps1') }
    if ($Suite -eq 'b4-unified') { & (Join-Path $PSScriptRoot 'Test-AccountRecoveryDiagnostics.ps1') }
    $script = Join-Path $PSScriptRoot 'oms-postgres.sh'
    $linuxScript = & wsl.exe -d Ubuntu --exec wslpath -a $script
    if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve runner path in Ubuntu.' }
    & wsl.exe -d Ubuntu -u root -- bash $linuxScript.Trim() $Suite
    if ($LASTEXITCODE -ne 0) { throw 'PostgreSQL acceptance failed; inspect the reported evidence directory.' }
}
finally {
    $env:DOTNET_CLI_USE_MSBUILD_SERVER = $previous
}
