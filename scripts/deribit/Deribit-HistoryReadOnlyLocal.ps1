#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)]
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string]$ExpectedHead,
    [ValidatePattern('^[A-Z0-9]{1,16}$')]
    [string]$Currency = 'BTC',
    [ValidateRange(1,7)]
    [int]$Days = 7,
    [string]$Repo = 'D:\Projects\TradeOps-v2'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'WINDOWS_DPAPI_REQUIRED'
}
if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
    throw 'LOCALAPPDATA_REQUIRED'
}
$branch = 'TradeOps/to-v2-26-deribit-readonly-trade-history'
$vault = Join-Path $env:LOCALAPPDATA 'TradeOps\Credentials\Deribit.Mainnet.ReadOnly.dpapi.json'
foreach ($path in @(
    (Join-Path $env:LOCALAPPDATA 'TradeOps'),
    (Join-Path $env:LOCALAPPDATA 'TradeOps\Credentials'),
    $vault
)) {
    if (Test-Path -LiteralPath $path) {
        $info = Get-Item -LiteralPath $path -Force
        if (($info.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'VAULT_REPARSE_POINT_REJECTED'
        }
    }
}
if (-not (Test-Path -LiteralPath $vault -PathType Leaf)) {
    throw 'DERIBIT_DPAPI_VAULT_MISSING'
}
if (-not (Test-Path -LiteralPath $Repo -PathType Container)) {
    throw 'TRADEOPS_REPO_MISSING'
}
$dotnetCmd = Get-Command dotnet.exe -ErrorAction SilentlyContinue
$dotnet = if ($dotnetCmd) { $dotnetCmd.Source } else { 'D:\DotNet\dotnet.exe' }
if (-not (Test-Path -LiteralPath $dotnet)) {
    throw 'DOTNET_NOT_FOUND'
}

$stage = Join-Path $env:TEMP (
    'TradeOps-Deribit-History-' + $ExpectedHead.Substring(0,12) + '-' + $PID)
$worktreeCreated = $false
$idSecure = $null
$secretSecure = $null
$id = $null
$secret = $null
try {
    & git.exe -C $Repo fetch origin $branch
    if ($LASTEXITCODE -ne 0) { throw 'GIT_FETCH_FAILED' }
    $actual = (& git.exe -C $Repo rev-parse FETCH_HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $actual -cne $ExpectedHead) {
        throw 'UNQUALIFIED_REMOTE_SHA'
    }
    if (Test-Path -LiteralPath $stage) {
        throw 'TEMPORARY_WORKTREE_ALREADY_EXISTS'
    }
    & git.exe -C $Repo worktree add --detach $stage $ExpectedHead
    if ($LASTEXITCODE -ne 0) { throw 'TEMPORARY_WORKTREE_FAILED' }
    $worktreeCreated = $true

    # Build before decrypting local DPAPI credentials.
    $project = Join-Path $stage 'tools\TradeOps.DeribitHistoryReadOnlySmoke\TradeOps.DeribitHistoryReadOnlySmoke.csproj'
    & $dotnet build $project -c Release
    if ($LASTEXITCODE -ne 0) { throw 'DERIBIT_HISTORY_BUILD_FAILED' }
    $dll = Join-Path $stage 'tools\TradeOps.DeribitHistoryReadOnlySmoke\bin\Release\net10.0\TradeOps.DeribitHistoryReadOnlySmoke.dll'
    if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) {
        throw 'DERIBIT_HISTORY_BINARY_NOT_FOUND'
    }

    $data = Get-Content -LiteralPath $vault -Raw | ConvertFrom-Json
    if (($data.schema -ne 1) -or
        ($data.environment -cne 'DeribitMainnetReadOnly') -or
        [string]::IsNullOrWhiteSpace($data.client_id_dpapi) -or
        [string]::IsNullOrWhiteSpace($data.client_secret_dpapi)) {
        throw 'DERIBIT_DPAPI_VAULT_INVALID'
    }

    $idSecure = ConvertTo-SecureString -String $data.client_id_dpapi
    $secretSecure = ConvertTo-SecureString -String $data.client_secret_dpapi
    $idCredential = [System.Management.Automation.PSCredential]::new('local', $idSecure)
    $secretCredential = [System.Management.Automation.PSCredential]::new('local', $secretSecure)
    $id = $idCredential.GetNetworkCredential().Password
    $secret = $secretCredential.GetNetworkCredential().Password
    if ([string]::IsNullOrWhiteSpace($id) -or
        [string]::IsNullOrWhiteSpace($secret)) {
        throw 'DERIBIT_DPAPI_DECRYPTION_EMPTY'
    }

    $env:DERIBIT_LIVE_READONLY_CLIENT_ID = $id
    $env:DERIBIT_LIVE_READONLY_CLIENT_SECRET = $secret
    $env:TRADEOPS_DERIBIT_LIVE_HISTORY_CONFIRM = 'READ_ONLY_MAINNET_HISTORY_TWO_MODES'
    Write-Host ('DERIBIT HISTORY VAULT UNLOCKED locally; verified_sha=' + $ExpectedHead)
    & $dotnet $dll $Currency ([string]$Days)
    if ($LASTEXITCODE -ne 0) {
        throw ('DERIBIT_HISTORY_READONLY_FAILED exit=' + $LASTEXITCODE)
    }
}
finally {
    Remove-Item Env:DERIBIT_LIVE_READONLY_CLIENT_ID -ErrorAction SilentlyContinue
    Remove-Item Env:DERIBIT_LIVE_READONLY_CLIENT_SECRET -ErrorAction SilentlyContinue
    Remove-Item Env:TRADEOPS_DERIBIT_LIVE_HISTORY_CONFIRM -ErrorAction SilentlyContinue
    $id = $null
    $secret = $null
    if ($idSecure) { $idSecure.Dispose() }
    if ($secretSecure) { $secretSecure.Dispose() }
    if ($worktreeCreated) {
        & git.exe -C $Repo worktree remove --force $stage
        if ($LASTEXITCODE -ne 0) {
            Write-Warning 'TEMPORARY_WORKTREE_CLEANUP_FAILED'
        }
    }
}
