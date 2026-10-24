#requires -Version 5.1
<#
.SYNOPSIS
  Runs the exact reviewed Deribit React terminal .NET backend against
  the owner's existing mainnet READ-ONLY DPAPI vault.
.DESCRIPTION
  Manual owner-only script. Verifies remote SHA, stages detached Git worktree,
  builds .NET API BEFORE decrypting DPAPI vault, forces BOTH live-trading
  switches to FALSE, then starts localhost API. React can connect separately.
  NEVER prints exchange credentials or modifies live exchange orders.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)]
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string]$ExpectedHead,
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
if (-not (Test-Path -LiteralPath $Repo -PathType Container)) {
    throw 'TRADEOPS_REPOSITORY_NOT_FOUND'
}

$branch = 'TradeOps/to-v2-38-deribit-react-terminal'
$vaultDir = Join-Path $env:LOCALAPPDATA 'TradeOps\Credentials'
$vault = Join-Path $vaultDir 'Deribit.Mainnet.ReadOnly.dpapi.json'
$dotnetFound = Get-Command dotnet.exe -ErrorAction SilentlyContinue
$dotnet = if ($dotnetFound) { $dotnetFound.Source } else { 'D:\DotNet\dotnet.exe' }
if (-not (Test-Path -LiteralPath $dotnet)) { throw 'DOTNET_SDK_MISSING' }

function Assert-RegularLocalPath([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) {
        throw 'DERIBIT_MAINNET_READONLY_VAULT_MISSING'
    }
    $entry = Get-Item -LiteralPath $Path -Force
    if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'DERIBIT_VAULT_REPARSE_POINT_REJECTED'
    }
}
Assert-RegularLocalPath $vaultDir
Assert-RegularLocalPath $vault

& git.exe -C $Repo fetch origin $branch
if ($LASTEXITCODE -ne 0) { throw 'GIT_FETCH_FAILED' }
$actual = (& git.exe -C $Repo rev-parse FETCH_HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $actual -cne $ExpectedHead) {
    throw 'SHA_MISMATCH_DO_NOT_DECRYPT_VAULT'
}

$stage = Join-Path $env:TEMP ('TradeOps-ReactTerminal-' + $ExpectedHead.Substring(0,12) + '-' + $PID)
if (Test-Path -LiteralPath $stage) { throw 'TERMINAL_STAGING_COLLISION' }

$created = $false
$idSecure = $null
$secretSecure = $null
$operatorSecure = $null
$ptr = [IntPtr]::Zero
$id = $null
$secret = $null
try {
    & git.exe -C $Repo worktree add --detach $stage $ExpectedHead
    if ($LASTEXITCODE -ne 0) { throw 'TERMINAL_WORKTREE_CREATE_FAILED' }
    $created = $true

    $project = Join-Path $stage 'apps\TradeOps.TerminalApi\TradeOps.TerminalApi.csproj'
    & $dotnet build $project -c Release
    if ($LASTEXITCODE -ne 0) { throw 'TRADEOPS_REACT_TERMINAL_DOTNET_BUILD_FAILED' }

    $vaultData = Get-Content -LiteralPath $vault -Raw | ConvertFrom-Json
    if ($vaultData.schema -ne 1 -or
        $vaultData.environment -cne 'DeribitMainnetReadOnly' -or
        [string]::IsNullOrWhiteSpace($vaultData.client_id_dpapi) -or
        [string]::IsNullOrWhiteSpace($vaultData.client_secret_dpapi)) {
        throw 'DERIBIT_READONLY_VAULT_INVALID'
    }

    $idSecure = ConvertTo-SecureString -String $vaultData.client_id_dpapi
    $secretSecure = ConvertTo-SecureString -String $vaultData.client_secret_dpapi
    $id = ([Management.Automation.PSCredential]::new('id', $idSecure)).GetNetworkCredential().Password
    $secret = ([Management.Automation.PSCredential]::new('secret', $secretSecure)).GetNetworkCredential().Password
    if ([string]::IsNullOrWhiteSpace($id) -or [string]::IsNullOrWhiteSpace($secret)) {
        throw 'DERIBIT_READONLY_VAULT_EMPTY'
    }
    $operatorSecure = Read-Host 'Create terminal operator passphrase (min 32 chars, enter same in React)' -AsSecureString
    if ($operatorSecure.Length -lt 32) { throw 'OPERATOR_PASSPHRASE_TOO_SHORT' }
    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($operatorSecure)
    $operatorKey = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr)
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr)
    $ptr = [IntPtr]::Zero

    $env:DeribitTerminal__OperatorKey = $operatorKey
    $env:DeribitTerminal__ClientId = $id
    $env:DeribitTerminal__ClientSecret = $secret
    $env:DeribitTerminal__Environment = 'mainnet'
    $env:DeribitTerminal__ExecutionEnabled = 'false'
    $env:DeribitTerminal__MainnetExecutionEnabled = 'false'
    $env:DeribitTerminal__MaxContracts = '10'
    $operatorKey = $null
    $id = $null
    $secret = $null

    Write-Host 'TRADEOPS_REACT_TERMINAL_MAINNET_READONLY_START trading_enabled=NO production_private_reads=YES'
    Write-Host ('TRADEOPS_REACT_TERMINAL_FRONTEND_SOURCE=' + (Join-Path $stage 'apps\trade-terminal'))
    Write-Host 'TRADEOPS_REACT_TERMINAL_API_URL=http://127.0.0.1:5058'
    Write-Host 'In a second PowerShell: cd to FRONTEND_SOURCE above; npm install; npm run dev'
    Write-Host 'Then open http://127.0.0.1:5173 and enter the operator passphrase.'
    Write-Host 'This foreground API terminates with Ctrl+C; trading remains disabled.'

    & $dotnet run --no-build -c Release --project $project --urls 'http://127.0.0.1:5058'
    if ($LASTEXITCODE -ne 0) { throw 'TERMINAL_API_TERMINATED_WITH_ERROR' }
}
finally {
    Remove-Item Env:DeribitTerminal__OperatorKey -ErrorAction SilentlyContinue
    Remove-Item Env:DeribitTerminal__ClientId -ErrorAction SilentlyContinue
    Remove-Item Env:DeribitTerminal__ClientSecret -ErrorAction SilentlyContinue
    Remove-Item Env:DeribitTerminal__Environment -ErrorAction SilentlyContinue
    Remove-Item Env:DeribitTerminal__ExecutionEnabled -ErrorAction SilentlyContinue
    Remove-Item Env:DeribitTerminal__MainnetExecutionEnabled -ErrorAction SilentlyContinue
    Remove-Item Env:DeribitTerminal__MaxContracts -ErrorAction SilentlyContinue

    $id = $null
    $secret = $null
    $vaultData = $null
    if ($ptr -ne [IntPtr]::Zero) {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr)
    }
    if ($idSecure) { $idSecure.Dispose() }
    if ($secretSecure) { $secretSecure.Dispose() }
    if ($operatorSecure) { $operatorSecure.Dispose() }
    if ($created) {
        & git.exe -C $Repo worktree remove --force $stage
        if ($LASTEXITCODE -ne 0) {
            Write-Warning 'DETACHED_TERMINAL_WORKTREE_CLEANUP_FAILED'
        }
    }
}
