#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string]$ExpectedHead,
    [string]$Repo = 'D:\Projects\TradeOps-v2',
    [Parameter(Mandatory = $true)]
    [ValidateSet('tradeops_evidence')]
    [string]$ApprovedDatabase,
    [Parameter(Mandatory = $true)]
    [switch]$ApproveNoLoginWriterInstallation
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $ApproveNoLoginWriterInstallation.IsPresent) {
    throw 'EXPLICIT_NOLOGIN_WRITER_OWNER_APPROVAL_REQUIRED'
}
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'WINDOWS_POWERSHELL_REQUIRED'
}
if (-not (Test-Path -LiteralPath $Repo -PathType Container)) {
    throw 'TRADEOPS_REPOSITORY_MISSING'
}

$branch = 'TradeOps/to-v2-34-persistent-evidence-writer-nologin'
& git.exe -C $Repo fetch origin $branch
if ($LASTEXITCODE -ne 0) { throw 'GIT_FETCH_FAILED' }
$fetched = (& git.exe -C $Repo rev-parse FETCH_HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $fetched -cne $ExpectedHead) {
    throw 'UNQUALIFIED_WRITER_INSTALL_SHA'
}

$stage = Join-Path $env:TEMP (
    'TradeOps-PGWriter-' + $PID + '-' + [Guid]::NewGuid().ToString('N')
)
if (Test-Path -LiteralPath $stage) {
    throw 'WRITER_STAGING_COLLISION'
}
New-Item -ItemType Directory -Path $stage -ErrorAction Stop | Out-Null

function Convert-PinnedFileToWsl {
    param([Parameter(Mandatory=$true)][string]$WindowsFile)
    $portable = $WindowsFile.Replace([char]92,[char]47)
    $paths = @(& wsl.exe -- wslpath -u -a $portable)
    if ($LASTEXITCODE -ne 0 -or $paths.Count -ne 1) {
        throw 'WSL_PATH_TRANSLATION_FAILED'
    }
    $converted = [string]$paths[0]
    if ([string]::IsNullOrWhiteSpace($converted) -or
        -not $converted.Trim().StartsWith('/')) {
        throw 'WSL_PATH_NOT_ABSOLUTE'
    }
    $converted = $converted.Trim()
    & wsl.exe -- test -f $converted
    if ($LASTEXITCODE -ne 0) {
        throw 'PINNED_STAGED_FILE_MISSING'
    }
    return $converted
}

try {
    $files = @(
        'deploy/deribit-evidence/install-writer-nologin.owner-gated.pg17.sh',
        'deploy/deribit-evidence/TO_V2_33_WRITER_ROLE_PROPOSED.sql'
    )
    $linuxFiles = @()
    foreach ($file in $files) {
        $gitObject = '{0}:{1}' -f $ExpectedHead, $file
        $source = @(git.exe -C $Repo show $gitObject)
        if ($LASTEXITCODE -ne 0 -or $source.Count -eq 0) {
            throw 'PINNED_SQL_OR_BASH_NOT_FOUND'
        }
        $target = Join-Path $stage ([IO.Path]::GetFileName($file))
        $utf8 = [System.Text.UTF8Encoding]::new($false)
        [IO.File]::WriteAllText($target, (($source -join [char]10) + [char]10), $utf8)
        $linuxFiles += Convert-PinnedFileToWsl -WindowsFile $target
    }
    & wsl.exe -- bash -n $linuxFiles[0]
    if ($LASTEXITCODE -ne 0) {
        throw 'PINNED_WRITER_INSTALL_BASH_SYNTAX_INVALID'
    }

    Write-Host ('TRADEOPS_PG17_WRITER_INSTALL_START database=' +
                $ApprovedDatabase + ' pinned_sha=' + $ExpectedHead)
    & wsl.exe -- bash $linuxFiles[0] $linuxFiles[1] 'I_APPROVE_INSTALL_NOLOGIN_WRITER_TRADEOPS_EVIDENCE'
    if ($LASTEXITCODE -ne 0) {
        throw ('PERSISTENT_WRITER_INSTALL_FAILED exit=' + $LASTEXITCODE)
    }
}
finally {
    # Only remove this newly created Windows staging directory.
    # Never delete a PostgreSQL role, database, volume or backup on failure.
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
}
