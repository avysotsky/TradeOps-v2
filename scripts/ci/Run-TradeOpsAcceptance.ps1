# Direct WSL Python launch avoids CRLF/Bash issues on Windows Git. One command: & 'D:\Projects\TradeOps-v2\scripts\ci\Run-TradeOpsAcceptance.ps1'
[CmdletBinding()]
param(
    [string]$Repository = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
)
$ErrorActionPreference = 'Stop'
$wslScript = (& wsl.exe --exec wslpath -u (Join-Path $PSScriptRoot 'local_acceptance.py'))
if ($LASTEXITCODE -ne 0) { throw 'Cannot translate script path to WSL' }
$wslRepo = (& wsl.exe --exec wslpath -u $Repository)
if ($LASTEXITCODE -ne 0) { throw 'Cannot translate repository path to WSL' }
& wsl.exe --exec python3 $wslScript all $wslRepo
if ($LASTEXITCODE -ne 0) { throw "CI-01 acceptance failed/blocked (exit=$LASTEXITCODE). See ~/tradeops-acceptance-results." }
