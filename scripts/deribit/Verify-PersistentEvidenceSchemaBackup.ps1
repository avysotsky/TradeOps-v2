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

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'WINDOWS_REQUIRED' }
if (-not (Test-Path -LiteralPath $Repo -PathType Container)) { throw 'TRADEOPS_REPOSITORY_MISSING' }

$branch = 'TradeOps/to-v2-32-pg-schema-backup-recovery'
& git.exe -C $Repo fetch origin $branch
if ($LASTEXITCODE -ne 0) { throw 'FETCH_FAILED' }
$fetched = (& git.exe -C $Repo rev-parse FETCH_HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $fetched -cne $ExpectedHead) { throw 'UNQUALIFIED_BACKUP_SHA' }

$stage = Join-Path $env:TEMP ('TradeOps-SchemaBackup-' + $PID + '-' + [Guid]::NewGuid().ToString('N'))
if (Test-Path -LiteralPath $stage) { throw 'STAGE_COLLISION' }
New-Item -ItemType Directory -Path $stage -ErrorAction Stop | Out-Null
try {
    $obj = '{0}:deploy/deribit-evidence/backup-verify-schema-pg17.sh' -f $ExpectedHead
    $source = @(git.exe -C $Repo show $obj)
    if ($LASTEXITCODE -ne 0 -or $source.Count -eq 0) { throw 'PINNED_SCRIPT_NOT_FOUND' }
    $script = Join-Path $stage 'backup-verify-schema-pg17.sh'
    $utf8 = [System.Text.UTF8Encoding]::new($false)
    [IO.File]::WriteAllText($script, (($source -join [char]10) + [char]10), $utf8)
    $portable = $script.Replace([char]92, [char]47)
    $paths = @(& wsl.exe -- wslpath -u -a $portable)
    if ($LASTEXITCODE -ne 0 -or $paths.Count -ne 1) { throw 'WSL_PATH_FAILED' }
    $wslPath = [string]$paths[0]
    if ([string]::IsNullOrWhiteSpace($wslPath) -or -not $wslPath.Trim().StartsWith('/')) {
        throw 'WSL_PATH_INVALID'
    }
    $wslPath = $wslPath.Trim()
    & wsl.exe -- test -f $wslPath
    if ($LASTEXITCODE -ne 0) { throw 'STAGED_SCRIPT_MISSING' }
    & wsl.exe -- bash -n $wslPath
    if ($LASTEXITCODE -ne 0) { throw 'BASH_SYNTAX_FAILED' }

    Write-Host ('TRADEOPS_PG17_SCHEMA_BACKUP_START pinned_sha=' + $ExpectedHead)
    & wsl.exe -- bash $wslPath
    if ($LASTEXITCODE -ne 0) { throw ('SCHEMA_BACKUP_RESTORE_FAILED exit=' + $LASTEXITCODE) }
}
finally {
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
}
