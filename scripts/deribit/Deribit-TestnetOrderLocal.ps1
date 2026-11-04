#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Save', 'Status', 'Run')][string]$Action = 'Status',
    [ValidatePattern('^[0-9a-f]{40}$')][string]$ExpectedHead,
    [switch]$ApproveOneTestnetOrderAndCancellation
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'WINDOWS_DPAPI_REQUIRED' }
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$local = Join-Path $repo '.tradeops/b3-testnet-order'
foreach ($path in @((Join-Path $repo '.tradeops'), $local)) {
    if ((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'LOCAL_REPARSE_POINT_REFUSED'
    }
}
$vault = Join-Path $local 'credential.dpapi.json'
if ((Test-Path -LiteralPath $vault) -and ((Get-Item -LiteralPath $vault -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'CREDENTIAL_REPARSE_POINT_REFUSED'
}
if ($Action -eq 'Status') {
    [ordered]@{ Environment='Testnet'; CredentialFileExists=(Test-Path -LiteralPath $vault);
        PreviousAttemptExists=(Test-Path -LiteralPath (Join-Path $local 'attempt/attempt.json')); MutatingCalls=0 } | ConvertTo-Json -Compress
    return
}
if ($Action -eq 'Save') {
    if (Test-Path -LiteralPath $vault) { throw 'TESTNET_CREDENTIAL_ALREADY_EXISTS' }
    New-Item -ItemType Directory -Force -Path $local | Out-Null
    $idSecure = Read-Host 'Deribit TESTNET client ID (hidden)' -AsSecureString
    $secretSecure = Read-Host 'Deribit TESTNET client secret (hidden)' -AsSecureString
    try {
        if ($idSecure.Length -eq 0 -or $secretSecure.Length -eq 0) { throw 'EMPTY_TESTNET_CREDENTIAL' }
        $encrypted = [ordered]@{ Schema=1; Environment='Testnet'; Purpose='ONE_ORDER_CAPTURE_QUALIFICATION';
            ClientIdDpapi=($idSecure | ConvertFrom-SecureString); ClientSecretDpapi=($secretSecure | ConvertFrom-SecureString) }
        [IO.File]::WriteAllText($vault, ($encrypted | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
        Write-Output 'TESTNET_CREDENTIAL_SAVED_ENCRYPTED; NO_NETWORK_OR_ORDER_CALLS'
    }
    finally { $idSecure.Dispose(); $secretSecure.Dispose() }
    return
}
if (!$ApproveOneTestnetOrderAndCancellation -or !$ExpectedHead) { throw 'EXPLICIT_TESTNET_APPROVAL_AND_EXACT_HEAD_REQUIRED' }
if (!(Test-Path -LiteralPath $vault)) { throw 'SEPARATE_TESTNET_CREDENTIAL_NOT_CONFIGURED' }
if (Test-Path -LiteralPath (Join-Path $local 'attempt/attempt.json')) { throw 'PREVIOUS_ATTEMPT_REQUIRES_REVIEW; NEVER_AUTO_REPEAT' }
function Assert-Source {
    $head = (& git -C $repo rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $head -cne $ExpectedHead) { throw 'SOURCE_SHA_MISMATCH' }
    & git -C $repo diff --quiet HEAD --
    if ($LASTEXITCODE -ne 0) { throw 'COMMITTED_SOURCE_REQUIRED' }
    $untracked = @(& git -C $repo ls-files --others --exclude-standard)
    if ($LASTEXITCODE -ne 0 -or @($untracked | Where-Object { $_ -cne 'src/TradeOps.Api/Properties/launchSettings.json' }).Count -ne 0) {
        throw 'UNTRACKED_SOURCE_REFUSED'
    }
}
Assert-Source
$previous = $env:DOTNET_CLI_USE_MSBUILD_SERVER
try {
    $env:DOTNET_CLI_USE_MSBUILD_SERVER='0'
    & D:\DotNet\dotnet.exe build (Join-Path $repo 'tools/TradeOps.DeribitTestnetOrderQualification/TradeOps.DeribitTestnetOrderQualification.csproj') `
        -c Release --no-restore -m:1 -nr:false -p:BuildInParallel=false -p:UseSharedCompilation=false *> (Join-Path $local 'build.log')
    if ($LASTEXITCODE -ne 0) { throw 'TESTNET_BUILD_FAILED; SEE_LOCAL_BUILD_LOG' }
}
finally { $env:DOTNET_CLI_USE_MSBUILD_SERVER=$previous }
$runner = (& wsl.exe -d Ubuntu --exec wslpath -a (Join-Path $repo 'scripts/testing/oms-postgres.sh')).Trim()
if ($LASTEXITCODE -ne 0 -or $runner -notmatch '^/[A-Za-z0-9_./-]+$') { throw 'WSL_RUNNER_PATH_REFUSED' }
$saved = Get-Content -LiteralPath $vault -Raw | ConvertFrom-Json
if ($saved.Schema -ne 1 -or $saved.Environment -cne 'Testnet' -or $saved.Purpose -cne 'ONE_ORDER_CAPTURE_QUALIFICATION') {
    throw 'TESTNET_VAULT_PURPOSE_MISMATCH'
}
$process = [Diagnostics.Process]::new()
$process.StartInfo = [Diagnostics.ProcessStartInfo]::new('wsl.exe', ('-d Ubuntu -u root -- bash ' + $runner + ' b3-live-testnet-order'))
$process.StartInfo.UseShellExecute=$false; $process.StartInfo.CreateNoWindow=$true
$process.StartInfo.RedirectStandardInput=$true; $process.StartInfo.RedirectStandardOutput=$true; $process.StartInfo.RedirectStandardError=$true
$idSecure=$null; $secretSecure=$null; $payload=$null; $id=$null; $secret=$null
try {
    $idSecure = ConvertTo-SecureString $saved.ClientIdDpapi
    $secretSecure = ConvertTo-SecureString $saved.ClientSecretDpapi
    $id = ([Management.Automation.PSCredential]::new('local',$idSecure)).GetNetworkCredential().Password
    $secret = ([Management.Automation.PSCredential]::new('local',$secretSecure)).GetNetworkCredential().Password
    $payload = [ordered]@{ Purpose='TESTNET_ONE_POSTONLY_ORDER_CANCEL'; Environment='Testnet';
        ApprovedOneOrderAndCancellation=$true; DedicatedTestAccount=$true; ClientId=$id; ClientSecret=$secret } | ConvertTo-Json -Compress
    if (!$process.Start()) { throw 'TESTNET_CHILD_START_FAILED' }
    $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
    $process.StandardInput.WriteLine($payload); $process.StandardInput.Close()
}
finally {
    $id=$null; $secret=$null; $payload=$null; $saved=$null
    if ($idSecure) { $idSecure.Dispose() }; if ($secretSecure) { $secretSecure.Dispose() }
}
# Do not kill the child on a parent timeout: the child may be cancelling its one order.
if (!$process.WaitForExit(240000)) { throw 'TESTNET_CHILD_STILL_RUNNING; DO_NOT_START_ANOTHER_ORDER; REVIEW_ATTEMPT_MARKER' }
$lines = @($stdout.GetAwaiter().GetResult() -split '\r?\n' | Where-Object { $_.StartsWith('TRADEOPS_TESTNET_ORDER_RESULT=') })
$null = $stderr.GetAwaiter().GetResult()
if ($lines.Count -ne 1) { throw 'TESTNET_RESULT_UNKNOWN; REVIEW_ATTEMPT_MARKER' }
$result = $lines[0].Substring('TRADEOPS_TESTNET_ORDER_RESULT='.Length) | ConvertFrom-Json
Assert-Source
$result | Add-Member -NotePropertyName SourceCommit -NotePropertyValue $ExpectedHead
[IO.File]::WriteAllText((Join-Path $local 'result.json'), ($result | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
$result | ConvertTo-Json -Depth 8 -Compress
if ($process.ExitCode -ne 0 -or $result.Status -cne 'TESTNET_ORDER_CAPTURE_PASS' -or $result.TradingReady -ne $false -or
    $result.CaptureSourceCommit -cne $ExpectedHead -or $result.EnvironmentStatus -cne 'PASS' -or $result.CleanupStatus -cne 'PASS') {
    throw 'TESTNET_QUALIFICATION_NOT_PASSED; NO_AUTOMATIC_REPEAT'
}
