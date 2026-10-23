#requires -Version 5.1
<#
.SYNOPSIS
  Locally enroll, reuse or remove encrypted Deribit read-only API credentials.
.DESCRIPTION
  Windows-only DPAPI CurrentUser vault outside the repository.
  No plaintext keys, passwords, access tokens or portfolio data are written
  to disk, GitHub, chat, command-line arguments or standard output.
  Run executes only the exact pinned SHA of the pre-existing read-only smoke.
  Not a broker-trading capability.
.EXAMPLE
  .\Deribit-ReadOnlyLocal.ps1 -Action Save
.EXAMPLE
  .\Deribit-ReadOnlyLocal.ps1 -Action Run
.EXAMPLE
  .\Deribit-ReadOnlyLocal.ps1 -Action Remove
#>
[CmdletBinding()]
param(
    [ValidateSet('Save', 'Run', 'Status', 'Remove')]
    [string]$Action = 'Run',

    [string]$Repo = 'D:\Projects\TradeOps-v2',
    [string]$Branch = 'TradeOps/to-v2-24-deribit-readonly-evidence-contract',
    [string]$ExpectedHead = 'd6e43490a25adcd3109dceeb444e797494fe4fbc'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'DPAPI_CURRENT_USER_REQUIRES_WINDOWS'
}

if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
    throw 'LOCALAPPDATA_NOT_AVAILABLE'
}

$vaultDir = Join-Path $env:LOCALAPPDATA 'TradeOps\Credentials'
$vaultFile = Join-Path $vaultDir 'Deribit.Mainnet.ReadOnly.dpapi.json'

function Assert-NotReparsePoint([string]$Path) {
    if (Test-Path -LiteralPath $Path) {
        $item = Get-Item -LiteralPath $Path -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'CREDENTIAL_VAULT_REPARSE_POINT_REJECTED'
        }
    }
}

function Set-UserOnlyAcl([string]$Path, [bool]$Directory) {
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    if ($Directory) {
        $grant = '*{0}:(OI)(CI)F' -f $sid
        $sysGrant = '*S-1-5-18:(OI)(CI)F'
    }
    else {
        $grant = '*{0}:F' -f $sid
        $sysGrant = '*S-1-5-18:F'
    }

    & icacls.exe $Path /inheritance:r /grant:r $grant $sysGrant 2>$null |
        Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'CREDENTIAL_VAULT_ACL_FAILED'
    }
}

function Get-PlainFromSecure([Security.SecureString]$Value) {
    $credential = [Security.PSCredential]::new('local-only', $Value)
    return $credential.GetNetworkCredential().Password
}

if ($Action -eq 'Status') {
    Assert-NotReparsePoint $vaultDir
    Assert-NotReparsePoint $vaultFile
    if (Test-Path -LiteralPath $vaultFile) {
        Write-Host 'DERIBIT READONLY VAULT configured=true (contents not displayed)'
    }
    else {
        Write-Host 'DERIBIT READONLY VAULT configured=false'
    }
    return
}

if ($Action -eq 'Remove') {
    Assert-NotReparsePoint $vaultDir
    Assert-NotReparsePoint $vaultFile
    if (Test-Path -LiteralPath $vaultFile) {
        Remove-Item -LiteralPath $vaultFile -Force
    }
    Write-Host 'DERIBIT READONLY VAULT REMOVED locally (revoke API key at Deribit separately)'
    return
}

if ($Action -eq 'Save') {
    $clientIdSecure = $null
    $clientSecretSecure = $null
    $temporaryFile = $null
    try {
        Write-Host 'Store a DERIBIT MAINNET READ-ONLY key only.'
        Write-Host 'Required permissions: account:read, trade:read; wallet:none; NO read_write scopes.'
        $clientIdSecure = Read-Host 'Deribit Client ID (hidden)' -AsSecureString
        $clientSecretSecure = Read-Host 'Deribit Client Secret (hidden)' -AsSecureString

        if ($clientIdSecure.Length -eq 0 -or $clientSecretSecure.Length -eq 0) {
            throw 'EMPTY_DERIBIT_CREDENTIAL_INPUT'
        }

        $root = Join-Path $env:LOCALAPPDATA 'TradeOps'
        Assert-NotReparsePoint $root
        Assert-NotReparsePoint $vaultDir
        Assert-NotReparsePoint $vaultFile

        if (-not (Test-Path -LiteralPath $vaultDir)) {
            New-Item -ItemType Directory -Path $vaultDir -Force | Out-Null
        }
        Set-UserOnlyAcl $vaultDir $true

        # Deliberately omit -Key/-SecureKey: PowerShell uses Windows
        # CurrentUser DPAPI, not portable AES or plaintext.
        $item = [ordered]@{
            schema = 1
            environment = 'DeribitMainnetReadOnly'
            client_id_dpapi = ConvertFrom-SecureString -SecureString $clientIdSecure
            client_secret_dpapi = ConvertFrom-SecureString -SecureString $clientSecretSecure
        }

        $json = $item | ConvertTo-Json -Compress -Depth 3
        $temporaryFile = Join-Path $vaultDir (
            'Deribit.Mainnet.ReadOnly.' + [Guid]::NewGuid().ToString('N') + '.tmp')

        [IO.File]::WriteAllText(
            $temporaryFile, $json, ([System.Text.UTF8Encoding]::new($false)))
        Set-UserOnlyAcl $temporaryFile $false
        Move-Item -LiteralPath $temporaryFile -Destination $vaultFile -Force
        $temporaryFile = $null
        Set-UserOnlyAcl $vaultFile $false

        Write-Host 'DERIBIT READONLY VAULT SAVED encryption=WindowsDPAPI scope=CurrentUser'
        Write-Host ('Vault path: ' + $vaultFile)
        Write-Host 'No further Client ID/Secret entry is required for approved read-only runs.'
    }
    finally {
        if ($temporaryFile -and (Test-Path -LiteralPath $temporaryFile)) {
            Remove-Item -LiteralPath $temporaryFile -Force -ErrorAction SilentlyContinue
        }
        if ($clientIdSecure) { $clientIdSecure.Dispose() }
        if ($clientSecretSecure) { $clientSecretSecure.Dispose() }
    }
    return
}

# Action=Run. Validate checked-in source and build it BEFORE decrypting
# or exporting credentials to this process / the short-lived .NET child.
if ($ExpectedHead -notmatch '^[0-9a-f]{40}$') {
    throw 'EXPECTED_SHA_MUST_BE_40_LOWERCASE_HEX_CHARACTERS'
}
if (-not (Test-Path -LiteralPath $Repo -PathType Container)) {
    throw 'TRADEOPS_REPOSITORY_NOT_FOUND'
}
Assert-NotReparsePoint $vaultDir
Assert-NotReparsePoint $vaultFile
if (-not (Test-Path -LiteralPath $vaultFile -PathType Leaf)) {
    throw 'CREDENTIAL_VAULT_MISSING: run -Action Save once'
}

$dotnetCommand = Get-Command dotnet.exe -ErrorAction SilentlyContinue
$dotnet = if ($dotnetCommand) { $dotnetCommand.Source } else { 'D:\DotNet\dotnet.exe' }
if (-not (Test-Path -LiteralPath $dotnet)) {
    throw 'DOTNET_SDK_NOT_FOUND'
}

$stage = Join-Path $env:TEMP ('TradeOps-Deribit-' + $ExpectedHead.Substring(0,12) + '-' + $PID)
$worktreeCreated = $false
$idSecure = $null
$secretSecure = $null
$id = $null
$secret = $null

try {
    & git.exe -C $Repo fetch origin $Branch
    if ($LASTEXITCODE -ne 0) { throw 'GIT_FETCH_FAILED' }

    $remoteHead = (& git.exe -C $Repo rev-parse FETCH_HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $remoteHead -cne $ExpectedHead) {
        throw 'REMOTE_HEAD_MISMATCH: do not run unreviewed source'
    }

    if (Test-Path -LiteralPath $stage) {
        throw 'TEMPORARY_WORKTREE_ALREADY_EXISTS'
    }

    & git.exe -C $Repo worktree add --detach $stage $ExpectedHead
    if ($LASTEXITCODE -ne 0) { throw 'WORKTREE_CREATE_FAILED' }
    $worktreeCreated = $true

    $project = Join-Path $stage 'tools\TradeOps.DeribitLiveReadOnlySmoke\TradeOps.DeribitLiveReadOnlySmoke.csproj'
    & $dotnet build $project -c Release
    if ($LASTEXITCODE -ne 0) { throw 'READONLY_BUILD_FAILED' }

    $dll = Join-Path $stage 'tools\TradeOps.DeribitLiveReadOnlySmoke\bin\Release\net10.0\TradeOps.DeribitLiveReadOnlySmoke.dll'
    if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) {
        throw 'READONLY_BINARY_NOT_FOUND'
    }

    $doc = Get-Content -LiteralPath $vaultFile -Raw | ConvertFrom-Json
    if ($doc.schema -ne 1
        -or $doc.environment -cne 'DeribitMainnetReadOnly'
        -or [string]::IsNullOrWhiteSpace($doc.client_id_dpapi)
        -or [string]::IsNullOrWhiteSpace($doc.client_secret_dpapi)) {
        throw 'CREDENTIAL_VAULT_INVALID_FORMAT'
    }

    $idSecure = ConvertTo-SecureString -String $doc.client_id_dpapi
    $secretSecure = ConvertTo-SecureString -String $doc.client_secret_dpapi
    if ($idSecure.Length -eq 0 -or $secretSecure.Length -eq 0) {
        throw 'CREDENTIAL_VAULT_EMPTY_AFTER_DECRYPTION'
    }
    $id = Get-PlainFromSecure $idSecure
    $secret = Get-PlainFromSecure $secretSecure

    $env:DERIBIT_LIVE_READONLY_CLIENT_ID = $id
    $env:DERIBIT_LIVE_READONLY_CLIENT_SECRET = $secret
    $env:TRADEOPS_DERIBIT_LIVE_CONFIRM = 'READ_ONLY_MAINNET_THREE_CALLS'

    Write-Host ('DERIBIT READONLY VAULT UNLOCKED locally; verified_sha=' + $ExpectedHead)
    & $dotnet $dll
    if ($LASTEXITCODE -ne 0) {
        throw ('DERIBIT_READONLY_SMOKE_FAILED exit=' + $LASTEXITCODE)
    }
}
finally {
    Remove-Item Env:DERIBIT_LIVE_READONLY_CLIENT_ID -ErrorAction SilentlyContinue
    Remove-Item Env:DERIBIT_LIVE_READONLY_CLIENT_SECRET -ErrorAction SilentlyContinue
    Remove-Item Env:TRADEOPS_DERIBIT_LIVE_CONFIRM -ErrorAction SilentlyContinue
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
