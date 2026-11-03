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

$branch = 'TradeOps/to-v2-33-pg-evidence-writer-role-rehearsal'
& git.exe -C $Repo fetch origin $branch
if ($LASTEXITCODE -ne 0) { throw 'FETCH_FAILED' }
$fetched = (& git.exe -C $Repo rev-parse FETCH_HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $fetched -cne $ExpectedHead) {
    throw 'UNQUALIFIED_WRITER_ROLE_SHA'
}

$stage = Join-Path $env:TEMP (
    'TradeOps-WriterRehearsal-' + $PID + '-' + [Guid]::NewGuid().ToString('N')
)
if (Test-Path -LiteralPath $stage) { throw 'STAGE_ALREADY_EXISTS' }
New-Item -ItemType Directory -Path $stage -ErrorAction Stop | Out-Null
try {
    $files = @(
        'deploy/deribit-evidence/rehearse-writer-role.pg17.sh',
        'scripts/deribit/sql/TO_V2_28_BROKER_EVIDENCE_SCHEMA_PROPOSED.sql',
        'deploy/deribit-evidence/TO_V2_33_WRITER_ROLE_PROPOSED.sql',
        'deploy/deribit-evidence/TO_V2_33_WRITER_PERMISSIONS_SYNTHETIC.sql'
    )
    $linux = @()
    foreach ($file in $files) {
        $sourceRef = '{0}:{1}' -f $ExpectedHead, $file
        $lines = @(git.exe -C $Repo show $sourceRef)
        if ($LASTEXITCODE -ne 0 -or $lines.Count -eq 0) {
            throw 'PINNED_FILE_MISSING'
        }
        $path = Join-Path $stage ([IO.Path]::GetFileName($file))
        $utf8 = [System.Text.UTF8Encoding]::new($false)
        [IO.File]::WriteAllText($path, (($lines -join [char]10) + [char]10), $utf8)

        $portable = $path.Replace([char]92, [char]47)
        $converted = @(& wsl.exe -- wslpath -u -a $portable)
        if ($LASTEXITCODE -ne 0 -or $converted.Count -ne 1) {
            throw 'WSL_PATH_TRANSLATION_FAILED'
        }
        $resultPath = [string]$converted[0]
        if ([string]::IsNullOrWhiteSpace($resultPath) -or
            -not $resultPath.Trim().StartsWith('/')) {
            throw 'WSL_PATH_INVALID'
        }
        $resultPath = $resultPath.Trim()
        & wsl.exe -- test -f $resultPath
        if ($LASTEXITCODE -ne 0) { throw 'WSL_STAGED_FILE_NOT_FOUND' }
        $linux += $resultPath
    }

    & wsl.exe -- bash -n $linux[0]
    if ($LASTEXITCODE -ne 0) { throw 'WRITER_REHEARSAL_BASH_SYNTAX_FAILED' }

    Write-Host ('TRADEOPS_PG17_WRITER_ROLE_REHEARSAL_START pinned_sha=' + $ExpectedHead)
    & wsl.exe -- bash $linux[0] $linux[1] $linux[2] $linux[3]
    if ($LASTEXITCODE -ne 0) {
        throw ('WRITER_ROLE_REHEARSAL_FAILED exit=' + $LASTEXITCODE)
    }
}
finally {
    # Remove only this private Windows staging path, no Docker resources.
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
}
