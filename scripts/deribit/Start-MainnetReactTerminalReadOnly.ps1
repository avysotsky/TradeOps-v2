#requires -Version 5.1
<#
.SYNOPSIS
  Runs the exact reviewed Deribit React terminal .NET backend against
  the owner's existing mainnet READ-ONLY DPAPI vault.
.DESCRIPTION
  Manual owner-only script. Verifies remote SHA, stages detached Git worktree,
  builds .NET and React BEFORE decrypting DPAPI vault, forces BOTH live-trading
  switches to FALSE, and starts BOTH localhost processes. Operator key is
  randomly generated per run; no repeated password or separate npm terminal.
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
$backend = $null
$frontendProcess = $null
$operatorKey = $null
$random = $null
$bytes = $null
$id = $null
$secret = $null
try {
    & git.exe -C $Repo worktree add --detach $stage $ExpectedHead
    if ($LASTEXITCODE -ne 0) { throw 'TERMINAL_WORKTREE_CREATE_FAILED' }
    $created = $true

    $project = Join-Path $stage 'apps\TradeOps.TerminalApi\TradeOps.TerminalApi.csproj'
    & $dotnet build $project -c Release
    if ($LASTEXITCODE -ne 0) { throw 'TRADEOPS_REACT_TERMINAL_DOTNET_BUILD_FAILED' }

    # Compile React before opening ANY broker credentials.
    $frontendRoot = Join-Path $stage 'apps\trade-terminal'
    $nodeFound = Get-Command node.exe -ErrorAction SilentlyContinue
    $npmFound = Get-Command npm.cmd -ErrorAction SilentlyContinue
    if (-not $nodeFound -or -not $npmFound) {
        throw 'NODE24_NPM_NOT_FOUND'
    }
    Push-Location -LiteralPath $frontendRoot
    try {
        & $npmFound.Source install --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) { throw 'REACT_NPM_INSTALL_FAILED' }
        & $npmFound.Source run build
        if ($LASTEXITCODE -ne 0) { throw 'REACT_BUILD_FAILED' }
    }
    finally {
        Pop-Location
    }
    $viteCli = Join-Path $frontendRoot 'node_modules\vite\bin\vite.js'
    if (-not (Test-Path -LiteralPath $viteCli -PathType Leaf)) {
        throw 'VITE_CLI_NOT_FOUND'
    }

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
    # Generate an ephemeral 384-bit operator token automatically.
    # It exists only in process memory; nothing to enter or remember.
    $bytes = New-Object byte[] 48
    $random = [Security.Cryptography.RandomNumberGenerator]::Create()
    $random.GetBytes($bytes)
    $operatorKey = [Convert]::ToBase64String($bytes)
    [Array]::Clear($bytes, 0, $bytes.Length)
    $bytes = $null
    $random.Dispose()
    $random = $null

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

    # Spawn backend with its private credentials in its own child process.
    $apiRoot = Join-Path $stage 'apps\TradeOps.TerminalApi'
    $dll = Join-Path $apiRoot 'bin\Release\net10.0\TradeOps.TerminalApi.dll'
    if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) {
        throw 'STANDALONE_TERMINAL_API_DLL_NOT_FOUND'
    }
    $backend = Start-Process -FilePath $dotnet -WorkingDirectory $apiRoot -ArgumentList ('"{0}"' -f $dll) -PassThru -NoNewWindow
    if (-not $backend -or $backend.HasExited) {
        throw 'TERMINAL_API_START_FAILED'
    }

    # Clear exchange credentials before starting Vite. Only the .NET API
    # process retains them; the front end process can NEVER inherit them.
    Remove-Item Env:DeribitTerminal__ClientId -ErrorAction SilentlyContinue
    Remove-Item Env:DeribitTerminal__ClientSecret -ErrorAction SilentlyContinue
    Remove-Item Env:DeribitTerminal__OperatorKey -ErrorAction SilentlyContinue
    Remove-Item Env:DeribitTerminal__Environment -ErrorAction SilentlyContinue
    Remove-Item Env:DeribitTerminal__ExecutionEnabled -ErrorAction SilentlyContinue
    Remove-Item Env:DeribitTerminal__MainnetExecutionEnabled -ErrorAction SilentlyContinue
    Remove-Item Env:DeribitTerminal__MaxContracts -ErrorAction SilentlyContinue
    $id = $null
    $secret = $null
    $vaultData = $null

    # Vite gets ONLY the ephemeral operator key. Vite's localhost proxy
    # adds it to GET requests on the server, not to browser JavaScript.
    $env:TRADEOPS_TERMINAL_OPERATOR_KEY = $operatorKey
    $frontendProcess = Start-Process -FilePath $nodeFound.Source -WorkingDirectory $frontendRoot -ArgumentList ('"{0}" --host 127.0.0.1 --port 5173 --strictPort' -f $viteCli) -PassThru -NoNewWindow
    Remove-Item Env:TRADEOPS_TERMINAL_OPERATOR_KEY -ErrorAction SilentlyContinue
    $operatorKey = $null
    if (-not $frontendProcess -or $frontendProcess.HasExited) {
        throw 'REACT_VITE_START_FAILED'
    }

    $ready = $false
    foreach ($attempt in 1..40) {
        if ($backend.HasExited -or $frontendProcess.HasExited) {
            throw 'TERMINAL_CHILD_PROCESS_EXITED'
        }
        $tcp = New-Object System.Net.Sockets.TcpClient
        try {
            $tcp.Connect('127.0.0.1', 5173)
            $ready = $true
            break
        }
        catch {
            Start-Sleep -Milliseconds 500
        }
        finally {
            $tcp.Dispose()
        }
    }
    if (-not $ready) { throw 'REACT_LOCALHOST_NOT_READY' }

    Write-Host 'TRADEOPS_REACT_TERMINAL_MAINNET_READONLY_PASS auto_operator_auth=YES production_private_reads=YES live_execution=DISABLED'
    Write-Host 'TRADEOPS_REACT_TERMINAL_URL=http://127.0.0.1:5173'
    Write-Host 'No operator password and no second PowerShell are required.'
    Write-Host 'Press Ctrl+C to stop backend and React.'
    Start-Process 'http://127.0.0.1:5173'

    while (-not $backend.HasExited -and -not $frontendProcess.HasExited) {
        Start-Sleep -Milliseconds 1000
    }
    throw 'TERMINAL_PROCESS_STOPPED_UNEXPECTEDLY'
}
finally {
    # Stop only processes created by this launcher, never other services.
    if ($frontendProcess -and -not $frontendProcess.HasExited) {
        Stop-Process -Id $frontendProcess.Id -Force -ErrorAction SilentlyContinue
    }
    if ($backend -and -not $backend.HasExited) {
        Stop-Process -Id $backend.Id -Force -ErrorAction SilentlyContinue
    }
    foreach ($name in @(
        'DeribitTerminal__OperatorKey',
        'DeribitTerminal__ClientId',
        'DeribitTerminal__ClientSecret',
        'DeribitTerminal__Environment',
        'DeribitTerminal__ExecutionEnabled',
        'DeribitTerminal__MainnetExecutionEnabled',
        'DeribitTerminal__MaxContracts',
        'TRADEOPS_TERMINAL_OPERATOR_KEY'
    )) {
        Remove-Item -Path ('Env:' + $name) -ErrorAction SilentlyContinue
    }
    $id = $null
    $secret = $null
    $operatorKey = $null
    $vaultData = $null
    if ($bytes) { [Array]::Clear($bytes, 0, $bytes.Length) }
    if ($random) { $random.Dispose() }
    if ($idSecure) { $idSecure.Dispose() }
    if ($secretSecure) { $secretSecure.Dispose() }
    if ($created) {
        & git.exe -C $Repo worktree remove --force $stage
        if ($LASTEXITCODE -ne 0) {
            Write-Warning 'DETACHED_TERMINAL_WORKTREE_CLEANUP_FAILED'
        }
    }
}
