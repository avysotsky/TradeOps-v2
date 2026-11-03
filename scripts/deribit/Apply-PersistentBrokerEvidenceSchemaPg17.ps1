#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)]
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string]$ExpectedHead,
    [string]$Repo = 'D:\Projects\TradeOps-v2',
    [Parameter(Mandatory=$true)]
    [ValidateSet('tradeops_evidence')]
    [string]$ApprovedDatabase,
    [Parameter(Mandatory=$true)]
    [switch]$ApproveMigration
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $ApproveMigration.IsPresent) { throw 'EXPLICIT_OWNER_MIGRATION_APPROVAL_REQUIRED' }
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'WINDOWS_REQUIRED' }
if (-not (Test-Path -LiteralPath $Repo -PathType Container)) { throw 'REPO_MISSING' }

$branch = 'TradeOps/to-v2-31-persistent-evidence-schema-owner-gated'
& git.exe -C $Repo fetch origin $branch
if ($LASTEXITCODE -ne 0) { throw 'FETCH_FAILED' }
$fetched = (& git.exe -C $Repo rev-parse FETCH_HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $fetched -cne $ExpectedHead) {
    throw 'MIGRATION_HEAD_SHA_MISMATCH'
}

$stage = Join-Path $env:TEMP ('TradeOps-ApprovedSchema-' + $PID + '-' + [Guid]::NewGuid().ToString('N'))
if (Test-Path -LiteralPath $stage) { throw 'STAGE_ALREADY_EXISTS' }
New-Item -ItemType Directory -Path $stage -ErrorAction Stop | Out-Null

function Convert-ToWslPath {
    param([string]$WindowsFile)
    $portable = $WindowsFile.Replace([char]92, [char]47)
    $lines = @(& wsl.exe -- wslpath -u -a $portable)
    if ($LASTEXITCODE -ne 0 -or $lines.Count -ne 1) { throw 'WSLPATH_FAILED' }
    $converted = [string]$lines[0]
    if ([string]::IsNullOrWhiteSpace($converted) -or
        -not $converted.Trim().StartsWith('/')) {
        throw 'WSLPATH_INVALID'
    }
    $path = $converted.Trim()
    & wsl.exe -- test -f $path
    if ($LASTEXITCODE -ne 0) { throw 'WSL_FILE_MISSING' }
    return $path
}

try {
    $pinnedPaths = @(
        'deploy/deribit-evidence/apply-evidence-schema.owner-gated.pg17.sh',
        'scripts/deribit/sql/TO_V2_28_BROKER_EVIDENCE_SCHEMA_PROPOSED.sql'
    )
    $linuxPaths = @()
    foreach ($path in $pinnedPaths) {
        $gitObject = ('{0}:{1}' -f $ExpectedHead, $path)
        $lines = @(git.exe -C $Repo show $gitObject)
        if ($LASTEXITCODE -ne 0 -or $lines.Count -eq 0) { throw 'PINNED_MIGRATION_SOURCE_MISSING' }
        $file = Join-Path $stage ([IO.Path]::GetFileName($path))
        $utf8 = [System.Text.UTF8Encoding]::new($false)
        [IO.File]::WriteAllText($file, (($lines -join [char]10) + [char]10), $utf8)
        $linuxPaths += Convert-ToWslPath -WindowsFile $file
    }

    & wsl.exe -- bash -n $linuxPaths[0]
    if ($LASTEXITCODE -ne 0) { throw 'PINNED_MIGRATION_BASH_SYNTAX_INVALID' }

    Write-Host ('TRADEOPS_PERSISTENT_SCHEMA_MIGRATION_START database=' + $ApprovedDatabase + ' pinned_sha=' + $ExpectedHead)
    & wsl.exe -- bash $linuxPaths[0] $linuxPaths[1] 'I_APPROVE_SCHEMA_ONLY_TRADEOPS_EVIDENCE_PG17'
    if ($LASTEXITCODE -ne 0) {
        throw ('PERSISTENT_SCHEMA_MIGRATION_FAILED exit=' + $LASTEXITCODE)
    }
}
finally {
    # Only temporary Windows source staging is removed; neither a database
    # nor backup, Docker volume or persistent container is ever deleted.
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
}
