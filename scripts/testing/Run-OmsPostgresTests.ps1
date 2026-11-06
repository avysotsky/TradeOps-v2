param(
    [ValidateSet('targeted', 'b4-final', 'b2', 'b3', 'b3-stream', 'b3-host', 'b3-session', 'b3-inbox', 'b3-testnet-order', 'b3-mainnet-eth', 'b4-order', 'b4-order-reducer', 'b4-reader', 'b4-binding', 'b4-collector', 'b4-known-orders', 'b4-gate', 'b4-replay', 'b4-replay-gate', 'b4-account', 'b4-account-host', 'b4-account-manifest', 'b4-account-assessment', 'b4-unified', 'b4-terminal', 'b4-ledger', 'b4-receipts', 'b4-bounded', 'fixture')][string]$Suite = 'targeted',
    [string]$Filter,
    [int]$ExpectedTests,
    [switch]$IncludeLegacyPostgres,
    [string]$Dotnet = 'D:\DotNet\dotnet.exe'
)
$ErrorActionPreference = 'Stop'
if ($Suite -eq 'targeted' -and ([string]::IsNullOrWhiteSpace($Filter) -or $ExpectedTests -lt 1)) {
    throw 'Targeted checks require -Filter and a positive -ExpectedTests count. Select only the affected behavior during B4 development.'
}
if ($Suite -eq 'b4-final' -and $ExpectedTests -lt 1) { throw 'Final B4 acceptance requires the exact executed full-suite test count, including expanded data cases.' }
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
    if ($buildResult -ne 0) { Get-Content -LiteralPath $log -Tail 35; throw "Build failed: $log" }
    Get-Content -LiteralPath $log -Tail 4
    if ($Suite -in @('b4-account-host','b4-account-manifest')) { & (Join-Path $PSScriptRoot 'Test-AccountSnapshotDiagnostics.ps1') }
    if ($Suite -in @('b4-unified','b4-terminal','b4-ledger','b4-receipts')) { & (Join-Path $PSScriptRoot 'Test-AccountRecoveryDiagnostics.ps1') }
    if ($Suite -in @('b4-bounded','b4-final')) { & (Join-Path $PSScriptRoot 'Test-BoundedOmsDiagnostics.ps1'); & (Join-Path $PSScriptRoot 'Test-AccountRecoveryDiagnostics.ps1') }
    $script = Join-Path $PSScriptRoot 'oms-postgres.sh'
    $linuxScript = & wsl.exe -d Ubuntu --exec wslpath -a $script
    if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve runner path in Ubuntu.' }
    $runnerArguments = @($Suite)
    if ($Suite -eq 'targeted') { $runnerArguments += @($Filter, [string]$ExpectedTests) }
    if ($Suite -eq 'targeted' -and $IncludeLegacyPostgres) { $runnerArguments += 'legacy-postgres' }
    if ($Suite -eq 'b4-final') { $runnerArguments += [string]$ExpectedTests }
    & wsl.exe -d Ubuntu -u root --exec bash $linuxScript.Trim() @runnerArguments
    if ($LASTEXITCODE -ne 0) { throw 'PostgreSQL acceptance failed; inspect the reported evidence directory.' }
}
finally {
    $env:DOTNET_CLI_USE_MSBUILD_SERVER = $previous
}
