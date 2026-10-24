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
    throw 'WINDOWS_REQUIRED'
}
if (-not (Test-Path -LiteralPath $Repo -PathType Container)) {
    throw 'TRADEOPS_REPOSITORY_MISSING'
}

$branch = 'TradeOps/to-v2-36-gpg-portable-encrypted-backup'
& git.exe -C $Repo fetch origin $branch
if ($LASTEXITCODE -ne 0) { throw 'GIT_FETCH_FAILED' }
$head = (& git.exe -C $Repo rev-parse FETCH_HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $head -cne $ExpectedHead) {
    throw 'UNQUALIFIED_ENCRYPTED_EXPORT_SHA'
}

$stage = Join-Path $env:TEMP (
    'TradeOps-GPGExport-' + $PID + '-' + [Guid]::NewGuid().ToString('N')
)
if (Test-Path -LiteralPath $stage) {
    throw 'STAGING_COLLISION'
}
New-Item -ItemType Directory -Path $stage -ErrorAction Stop | Out-Null
try {
    $object = '{0}:deploy/deribit-evidence/export-gpg-recovery-to-windows-d.sh' -f $ExpectedHead
    $lines = @(git.exe -C $Repo show $object)
    if ($LASTEXITCODE -ne 0 -or $lines.Count -eq 0) {
        throw 'PINNED_GPG_EXPORT_SOURCE_MISSING'
    }
    $localFile = Join-Path $stage 'export-gpg-recovery-to-windows-d.sh'
    $utf8 = [System.Text.UTF8Encoding]::new($false)
    [IO.File]::WriteAllText($localFile, (($lines -join [char]10) + [char]10), $utf8)
    $portable = $localFile.Replace([char]92, [char]47)
    $paths = @(& wsl.exe -u user -- wslpath -u -a $portable)
    if ($LASTEXITCODE -ne 0 -or $paths.Count -ne 1) {
        throw 'WSL_PATH_TRANSLATION_FAILED'
    }
    $linuxFile = [string]$paths[0]
    if ([string]::IsNullOrWhiteSpace($linuxFile) -or
        -not $linuxFile.Trim().StartsWith('/')) {
        throw 'WSL_PATH_INVALID'
    }
    $linuxFile = $linuxFile.Trim()
    & wsl.exe -u user -- test -f $linuxFile
    if ($LASTEXITCODE -ne 0) {
        throw 'STAGED_GPG_EXPORT_MISSING'
    }
    & wsl.exe -u user -- bash -n $linuxFile
    if ($LASTEXITCODE -ne 0) {
        throw 'GPG_EXPORT_BASH_SYNTAX_INVALID'
    }

    Write-Host ('TRADEOPS_GPG_RECOVERY_EXPORT_START pinned_sha=' + $ExpectedHead)
    Write-Host 'The passphrase will be requested inside WSL; do not paste it into this chat.'
    & wsl.exe -u user -- bash $linuxFile
    if ($LASTEXITCODE -ne 0) {
        throw ('GPG_EXPORT_FAILED exit=' + $LASTEXITCODE)
    }
}
finally {
    # Deletes only the transient source code staged in Windows Temp;
    # NEVER deletes the recovery bundle or persistent PostgreSQL.
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
}
