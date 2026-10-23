#requires -Version 5.1
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
    throw 'WINDOWS_POWERSHELL_REQUIRED'
}
if (-not (Test-Path -LiteralPath $Repo -PathType Container)) {
    throw 'TRADEOPS_REPOSITORY_MISSING'
}
if (-not (Get-Command git.exe -ErrorAction SilentlyContinue)) {
    throw 'WINDOWS_GIT_REQUIRED'
}
if (-not (Get-Command wsl.exe -ErrorAction SilentlyContinue)) {
    throw 'WSL_REQUIRED'
}

$branch = 'TradeOps/to-v2-28-deribit-real-evidence-pg-foundation'
& git.exe -C $Repo fetch origin $branch
if ($LASTEXITCODE -ne 0) { throw 'GIT_FETCH_FAILED' }
$actual = (& git.exe -C $Repo rev-parse FETCH_HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $actual -cne $ExpectedHead) {
    throw 'UNQUALIFIED_DEPLOYMENT_SHA'
}

$stage = Join-Path $env:TEMP (
    'TradeOps-EmptyPG17-' + $PID + '-' + [Guid]::NewGuid().ToString('N')
)
if (Test-Path -LiteralPath $stage) {
    throw 'STAGING_DIR_ALREADY_EXISTS'
}
New-Item -ItemType Directory -Path $stage -ErrorAction Stop | Out-Null

try {
    $items = @(
        @{
            Source = 'deploy/deribit-evidence/docker-compose.pg17.yml'
            FileName = 'docker-compose.pg17.yml'
        },
        @{
            Source = 'deploy/deribit-evidence/deploy-empty-pg17.sh'
            FileName = 'deploy-empty-pg17.sh'
        }
    )

    foreach ($item in $items) {
        $sourceRef = ('{0}:{1}' -f $ExpectedHead, $item.Source)
        $lines = @(& git.exe -C $Repo show $sourceRef)
        if ($LASTEXITCODE -ne 0 -or $lines.Count -eq 0) {
            throw 'PINNED_DEPLOYMENT_FILE_NOT_FOUND'
        }

        # LF + UTF-8 without BOM so WSL bash reads the source literally.
        $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
        [System.IO.File]::WriteAllText(
            (Join-Path $stage $item.FileName),
            (($lines -join "`n") + "`n"),
            $utf8NoBom
        )
    }

    # WSL command forwarding can consume Windows backslash characters.
    # Use forward-slash drive paths (C:/...) when invoking wslpath.
    # Capture its output BEFORE Trim() so native failures fail explicitly.
    function Convert-ToVerifiedWslPath {
        param(
            [Parameter(Mandatory=$true)]
            [string]$WindowsPath,
            [Parameter(Mandatory=$true)]
            [string]$ErrorCode
        )
        $portablePath = $WindowsPath.Replace([char]92, [char]47)
        $lines = @(& wsl.exe -- wslpath -u -a $portablePath)
        if ($LASTEXITCODE -ne 0 -or $lines.Count -ne 1) {
            throw $ErrorCode
        }
        $converted = [string]$lines[0]
        if ([string]::IsNullOrWhiteSpace($converted)) {
            throw $ErrorCode
        }
        $converted = $converted.Trim()
        if (-not $converted.StartsWith('/')) {
            throw $ErrorCode
        }
        return $converted
    }

    $composeWindows = Join-Path $stage 'docker-compose.pg17.yml'
    $scriptWindows = Join-Path $stage 'deploy-empty-pg17.sh'
    $composeWsl = Convert-ToVerifiedWslPath -WindowsPath $composeWindows -ErrorCode 'COMPOSE_WSL_PATH_FAILED'
    $scriptWsl = Convert-ToVerifiedWslPath -WindowsPath $scriptWindows -ErrorCode 'SCRIPT_WSL_PATH_FAILED'

    & wsl.exe -- test -f $composeWsl
    if ($LASTEXITCODE -ne 0) { throw 'COMPOSE_WSL_FILE_NOT_FOUND' }
    & wsl.exe -- test -f $scriptWsl
    if ($LASTEXITCODE -ne 0) { throw 'SCRIPT_WSL_FILE_NOT_FOUND' }

    & wsl.exe -- bash -n $scriptWsl
    if ($LASTEXITCODE -ne 0) {
        throw 'PINNED_BASH_DEPLOY_SYNTAX_INVALID'
    }

    Write-Host ('TRADEOPS_PG17_DEPLOY_START pinned_sha=' + $ExpectedHead)
    & wsl.exe -- bash $scriptWsl $composeWsl
    if ($LASTEXITCODE -ne 0) {
        throw ('EMPTY_POSTGRES_DEPLOY_FAILED exit=' + $LASTEXITCODE)
    }
}
finally {
    # Clean only this newly created temporary staging directory.
    # Never remove Docker containers, Docker volumes or DB data.
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
}
