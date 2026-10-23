#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
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

$branch = 'TradeOps/to-v2-29-persistent-pg-backup-restore'
& git.exe -C $Repo fetch origin $branch
if ($LASTEXITCODE -ne 0) { throw 'GIT_FETCH_FAILED' }
$actual = (& git.exe -C $Repo rev-parse FETCH_HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $actual -cne $ExpectedHead) {
    throw 'UNQUALIFIED_BACKUP_SCRIPT_SHA'
}

$stage = Join-Path $env:TEMP (
    'TradeOps-PG17Backup-' + $PID + '-' + [Guid]::NewGuid().ToString('N')
)
if (Test-Path -LiteralPath $stage) {
    throw 'BACKUP_STAGING_PATH_EXISTS'
}
New-Item -ItemType Directory -Path $stage -ErrorAction Stop | Out-Null
try {
    $sourceRef = '{0}:deploy/deribit-evidence/backup-verify-empty-pg17.sh' -f $ExpectedHead
    $source = @(git.exe -C $Repo show $sourceRef)
    if ($LASTEXITCODE -ne 0 -or $source.Count -eq 0) {
        throw 'PINNED_BACKUP_BASH_MISSING'
    }
    $localScript = Join-Path $stage 'backup-verify-empty-pg17.sh'
    $utf8 = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText(
        $localScript, (($source -join "`n") + "`n"), $utf8
    )

    $portableWindows = $localScript.Replace([char]92, [char]47)
    $convertedLines = @(& wsl.exe -- wslpath -u -a $portableWindows)
    if ($LASTEXITCODE -ne 0 -or $convertedLines.Count -ne 1) {
        throw 'BACKUP_WSLPATH_FAILED'
    }
    $wslScript = [string]$convertedLines[0]
    if ([string]::IsNullOrWhiteSpace($wslScript)) {
        throw 'BACKUP_WSLPATH_EMPTY'
    }
    $wslScript = $wslScript.Trim()
    if (-not $wslScript.StartsWith('/')) {
        throw 'BACKUP_WSLPATH_NOT_ABSOLUTE'
    }
    & wsl.exe -- test -f $wslScript
    if ($LASTEXITCODE -ne 0) { throw 'BACKUP_WSL_SCRIPT_MISSING' }
    & wsl.exe -- bash -n $wslScript
    if ($LASTEXITCODE -ne 0) { throw 'BACKUP_BASH_SYNTAX_FAILED' }

    Write-Host ('TRADEOPS_PG17_BACKUP_RESTORE_START pinned_sha=' + $ExpectedHead)
    & wsl.exe -- bash $wslScript
    if ($LASTEXITCODE -ne 0) {
        throw ('TRADEOPS_PG17_BACKUP_RESTORE_FAILED exit=' + $LASTEXITCODE)
    }
}
finally {
    # Delete only the newly created Windows temp staging directory.
    # Never remove backups, Docker volumes or persistent containers here.
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
}
