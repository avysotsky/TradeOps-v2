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

$branch = 'TradeOps/to-v2-35-pg-roles-acl-recovery'
& git.exe -C $Repo fetch origin $branch
if ($LASTEXITCODE -ne 0) { throw 'GIT_FETCH_FAILED' }
$head = (& git.exe -C $Repo rev-parse FETCH_HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $head -cne $ExpectedHead) { throw 'UNQUALIFIED_ACL_RECOVERY_SHA' }

$stage = Join-Path $env:TEMP ('TradeOps-ACLRecovery-' + $PID + '-' + [Guid]::NewGuid().ToString('N'))
if (Test-Path -LiteralPath $stage) { throw 'STAGING_COLLISION' }
New-Item -ItemType Directory -Path $stage -ErrorAction Stop | Out-Null

function Convert-ToVerifiedWslPath {
    param([Parameter(Mandatory=$true)][string]$WindowsFile)
    $portable = $WindowsFile.Replace([char]92, [char]47)
    $lines = @(& wsl.exe -- wslpath -u -a $portable)
    if ($LASTEXITCODE -ne 0 -or $lines.Count -ne 1) { throw 'WSLPATH_FAILED' }
    $converted = [string]$lines[0]
    if ([string]::IsNullOrWhiteSpace($converted) -or
        -not $converted.Trim().StartsWith('/')) { throw 'WSLPATH_INVALID' }
    $linuxFile = $converted.Trim()
    & wsl.exe -- test -f $linuxFile
    if ($LASTEXITCODE -ne 0) { throw 'STAGED_WSL_FILE_NOT_FOUND' }
    return $linuxFile
}

try {
    $files = @(
        'deploy/deribit-evidence/backup-restore-full-acl.pg17.sh',
        'deploy/deribit-evidence/restore-known-evidence-roles.pg17.sql'
    )
    $wslFiles = @()
    foreach ($file in $files) {
        $object = '{0}:{1}' -f $ExpectedHead, $file
        $lines = @(git.exe -C $Repo show $object)
        if ($LASTEXITCODE -ne 0 -or $lines.Count -eq 0) {
            throw 'PINNED_ACL_RECOVERY_OBJECT_MISSING'
        }
        $localFile = Join-Path $stage ([IO.Path]::GetFileName($file))
        $utf8 = [System.Text.UTF8Encoding]::new($false)
        [IO.File]::WriteAllText($localFile, (($lines -join [char]10) + [char]10), $utf8)
        $wslFiles += Convert-ToVerifiedWslPath -WindowsFile $localFile
    }
    & wsl.exe -- bash -n $wslFiles[0]
    if ($LASTEXITCODE -ne 0) { throw 'ACL_RECOVERY_BASH_SYNTAX_INVALID' }

    Write-Host ('TRADEOPS_PG17_ROLE_ACL_RECOVERY_START pinned_sha=' + $ExpectedHead)
    & wsl.exe -- bash $wslFiles[0] $wslFiles[1]
    if ($LASTEXITCODE -ne 0) {
        throw ('ACL_RECOVERY_FAILED exit=' + $LASTEXITCODE)
    }
}
finally {
    # Remove only our private Windows staging files, never Docker state.
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
}
