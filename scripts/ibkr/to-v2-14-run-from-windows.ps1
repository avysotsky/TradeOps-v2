# TradeOps-v2: reusable Windows launcher for isolated REAL PostgreSQL / API acceptance.
# Run against an exact WSL2-qualified Draft PR HEAD. No existing DB/services touched.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string] $ExpectedHead,

    # Branch is intentionally mandatory. Defaulting to TO-V2-14
    # caused a previous TO-V2-15 attempt to fetch the wrong PR head.
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^TradeOps/to-v2-[0-9]{2}-[a-z0-9-]+$')]
    [string] $Branch,

    [string] $Repository = 'D:\Projects\TradeOps-v2'
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Repository -PathType Container)) {
    throw "TradeOps repository not found: $Repository"
}

$parent = Split-Path -Parent $Repository
$stage = Join-Path $parent (
    'TradeOps-RealPg-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
)
$log = Join-Path $parent (
    'TradeOps-RealPg-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.log'
)

$added = $false
try {
    & git.exe -C $Repository fetch origin $Branch
    if ($LASTEXITCODE -ne 0) {
        throw 'GIT FETCH FAILED'
    }

    $fetchedHead = (& git.exe -C $Repository rev-parse FETCH_HEAD |
        Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $fetchedHead -cne $ExpectedHead) {
        throw "REMOTE HEAD MISMATCH branch=$Branch actual=$fetchedHead expected=$ExpectedHead"
    }

    if (Test-Path -LiteralPath $stage) {
        throw 'WORKTREE STAGING PATH ALREADY EXISTS'
    }

    & git.exe -C $Repository worktree add --detach $stage $ExpectedHead
    if ($LASTEXITCODE -ne 0) {
        throw 'WORKTREE CREATION FAILED'
    }
    $added = $true

    # Verify with WINDOWS Git. Linux Git cannot parse a Windows worktree
    # .git pointer containing D:/...; Bash is passed this verified SHA.
    $stagedHead = (& git.exe -C $stage rev-parse HEAD |
        Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $stagedHead -cne $ExpectedHead) {
        throw "STAGED WORKTREE HEAD MISMATCH: $stagedHead"
    }

    $linuxPath = (& wsl.exe --exec wslpath -u $stage |
        Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or -not $linuxPath) {
        throw 'WSL PATH CONVERSION FAILED'
    }

    # No user input enters the shell command except a validated SHA and a
    # locally generated stage name under the known repository parent.
    if ($linuxPath.Contains("'")) {
        throw 'UNSAFE WORKTREE PATH'
    }

    $command = "cd '$linuxPath' && " +
        "bash -n scripts/ibkr/to-v2-14-run-real-postgres-acceptance.sh && " +
        "bash scripts/ibkr/to-v2-14-run-real-postgres-acceptance.sh '$ExpectedHead'"

    $oldPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & wsl.exe --exec bash -lc $command 2>&1 |
            ForEach-Object { "$_" } |
            Tee-Object -FilePath $log

        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $oldPreference
    }

    Write-Host "REAL POSTGRES ACCEPTANCE EXIT: $exitCode"
    Write-Host "REAL POSTGRES ACCEPTANCE LOG: $log"

    if ($exitCode -ne 0) {
        throw "REAL POSTGRES ACCEPTANCE FAILED: $exitCode"
    }
}
finally {
    if ($added) {
        & git.exe -C $Repository worktree remove --force $stage
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "WORKTREE CLEANUP FAILED: $stage"
        }
    }
}
