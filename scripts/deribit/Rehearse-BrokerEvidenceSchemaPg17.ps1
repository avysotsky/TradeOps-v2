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
    throw 'WINDOWS_REQUIRED'
}
if (-not (Test-Path -LiteralPath $Repo -PathType Container)) {
    throw 'TRADEOPS_REPO_MISSING'
}
$branch = 'TradeOps/to-v2-30-real-pg-schema-rehearsal'
& git.exe -C $Repo fetch origin $branch
if ($LASTEXITCODE -ne 0) { throw 'GIT_FETCH_FAILED' }
$actual = (& git.exe -C $Repo rev-parse FETCH_HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $actual -cne $ExpectedHead) {
    throw 'UNQUALIFIED_REMOTE_SHA'
}

$stage = Join-Path $env:TEMP ('TradeOps-PGSchema-' + $PID + '-' + [Guid]::NewGuid().ToString('N'))
if (Test-Path -LiteralPath $stage) { throw 'STAGE_ALREADY_EXISTS' }
New-Item -ItemType Directory -Path $stage | Out-Null

function Get-VerifiedWslPath {
    param([string]$WindowsFile)
    $portable = $WindowsFile.Replace([char]92, [char]47)
    $lines = @(& wsl.exe -- wslpath -u -a $portable)
    if ($LASTEXITCODE -ne 0 -or $lines.Count -ne 1) {
        throw 'WSLPATH_FAILED'
    }
    $converted = [string]$lines[0]
    if ([string]::IsNullOrWhiteSpace($converted) -or
        -not $converted.Trim().StartsWith('/')) {
        throw 'WSLPATH_INVALID'
    }
    $wslPath = $converted.Trim()
    & wsl.exe -- test -f $wslPath
    if ($LASTEXITCODE -ne 0) { throw 'STAGED_WSL_FILE_MISSING' }
    return $wslPath
}
try {
    $paths = @(
        'deploy/deribit-evidence/rehearse-evidence-schema.pg17.sh',
        'deploy/deribit-evidence/verify-schema-invariants.synthetic.sql',
        'scripts/deribit/sql/TO_V2_28_BROKER_EVIDENCE_SCHEMA_PROPOSED.sql'
    )
    $wslPaths = @()
    foreach ($path in $paths) {
        $sourceRef = '{0}:{1}' -f $ExpectedHead, $path
        $lines = @(git.exe -C $Repo show $sourceRef)
        if ($LASTEXITCODE -ne 0 -or $lines.Count -eq 0) {
            throw 'PINNED_GIT_SOURCE_MISSING'
        }
        $target = Join-Path $stage ([IO.Path]::GetFileName($path))
        $utf8 = [System.Text.UTF8Encoding]::new($false)
        [IO.File]::WriteAllText($target, (($lines -join [char]10) + [char]10), $utf8)
        $wslPaths += Get-VerifiedWslPath -WindowsFile $target
    }

    & wsl.exe -- bash -n $wslPaths[0]
    if ($LASTEXITCODE -ne 0) { throw 'BASH_SYNTAX_INVALID' }

    Write-Host ('TRADEOPS_PG17_SCHEMA_REHEARSAL_START pinned_sha=' + $ExpectedHead)
    & wsl.exe -- bash $wslPaths[0] $wslPaths[2] $wslPaths[1]
    if ($LASTEXITCODE -ne 0) {
        throw ('PG_SCHEMA_REHEARSAL_FAILED exit=' + $LASTEXITCODE)
    }
}
finally {
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
}
