#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][ValidatePattern('^[0-9a-f]{40}$')][string]$ExpectedHead,
    [ValidateRange(30,60)][int]$ObserveSeconds = 45,
    [switch]$WithIsolatedInbox,
    [string]$Repo = 'D:\Projects\TradeOps-v2'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($Repo)
$evidence = Join-Path $root $(if ($WithIsolatedInbox) { '.tradeops/b3-live-inbox' } else { '.tradeops/b3-private-session' })
$observations = Join-Path $root '.tradeops/b3-account-binding'
$vaultRoot = Join-Path $env:LOCALAPPDATA 'TradeOps'
$vaultDir = Join-Path $vaultRoot 'Credentials'
$vaultPath = Join-Path $vaultDir 'Deribit.Mainnet.ReadOnly.dpapi.json'
$child = $null; $idSecure = $null; $secretSecure = $null
$clientId = $null; $clientSecret = $null; $payload = $null; $profilePlain = $null
$phase = 'SOURCE'

function Assert-RegularPath([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'REPARSE_POINT_REJECTED' }
}
function Read-Encrypted([string]$Path) {
    Assert-RegularPath $Path
    $doc = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if ($doc.schema -ne 1 -or $doc.encryption -cne 'WindowsDPAPI-CurrentUser') { throw 'ENCRYPTED_FORMAT' }
    $secure = ConvertTo-SecureString -String $doc.payload
    try { return ([Management.Automation.PSCredential]::new('local', $secure)).GetNetworkCredential().Password }
    finally { $secure.Dispose() }
}
try {
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'WINDOWS_REQUIRED' }
    Assert-RegularPath $root
    $head = (& git -C $root rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $head -cne $ExpectedHead) { throw 'SOURCE_SHA_MISMATCH' }
    & git -C $root diff --quiet HEAD --
    if ($LASTEXITCODE -ne 0) { throw 'TRACKED_SOURCE_CHANGED' }
    $untracked = @(& git -C $root ls-files --others --exclude-standard)
    if ($LASTEXITCODE -ne 0 -or @($untracked | Where-Object { $_ -cne 'src/TradeOps.Api/Properties/launchSettings.json' }).Count -ne 0) {
        throw 'UNREVIEWED_SOURCE_FILES'
    }
    New-Item -ItemType Directory -Force -Path $evidence | Out-Null
    Assert-RegularPath $evidence
    Assert-RegularPath $observations
    $phase = 'BUILD'
    $toolName = if ($WithIsolatedInbox) { 'TradeOps.DeribitLiveInboxQualification' } else { 'TradeOps.DeribitPrivateSessionProbe' }
    $project = Join-Path $root ("tools/$toolName/$toolName.csproj")
    $dll = Join-Path $root ("tools/$toolName/bin/Release/net10.0/$toolName.dll")
    if ($WithIsolatedInbox) {
        $linuxRunner = & wsl.exe -d Ubuntu --exec wslpath -a (Join-Path $root 'scripts/testing/oms-postgres.sh')
        if ($LASTEXITCODE -ne 0 -or @($linuxRunner).Count -ne 1) { throw 'WSL_RUNNER_PATH_FAILED' }
        $linuxRunner = $linuxRunner.Trim()
    }
    $buildLog = Join-Path $evidence 'launcher-build.log'
    $previousBuildServer = $env:DOTNET_CLI_USE_MSBUILD_SERVER
    try {
        $env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'
        & D:\DotNet\dotnet.exe build $project -c Release --no-restore -m:1 -nr:false -p:BuildInParallel=false -p:UseSharedCompilation=false *> $buildLog
        if ($LASTEXITCODE -ne 0) { throw 'PROBE_BUILD_FAILED' }
    } finally { $env:DOTNET_CLI_USE_MSBUILD_SERVER = $previousBuildServer }
    & git -C $root diff --quiet HEAD --
    if ($LASTEXITCODE -ne 0) { throw 'SOURCE_CHANGED_DURING_BUILD' }
    $phase = 'PROTECTED_BINDING'
    $statusPath = Join-Path $observations 'observation-result.json'
    Assert-RegularPath $statusPath
    $status = Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json
    if ($status.status -cne 'READ_ONLY_IDENTITY_OBSERVED' -or
        $status.encryptedObservation -cnotmatch '^account-observation\.[0-9a-f]{32}\.dpapi\.json$') { throw 'OBSERVATION_REQUIRED' }
    $observation = (Read-Encrypted (Join-Path $observations $status.encryptedObservation)) | ConvertFrom-Json
    if ($observation.schema -ne 1 -or $observation.provenance -cne 'AuthenticatedReadOnlyObservation' -or
        $observation.environment -cne 'Mainnet' -or $observation.remoteAccountType -cnotin @('main','subaccount') -or
        $observation.remoteAccountId -le 0 -or $observation.bindingActivated -ne $false) { throw 'UNSUPPORTED_OBSERVED_BINDING' }
    $age = [DateTimeOffset]::UtcNow - [DateTimeOffset]::Parse($observation.observedAtUtc)
    if ($age.TotalHours -lt 0 -or $age.TotalHours -gt 24) { throw 'OBSERVATION_EXPIRED' }
    foreach ($path in @($vaultRoot, $vaultDir, $vaultPath)) { Assert-RegularPath $path }
    $vault = Get-Content -LiteralPath $vaultPath -Raw | ConvertFrom-Json
    if ($vault.schema -ne 1 -or $vault.environment -cne 'DeribitMainnetReadOnly') { throw 'VAULT_SCHEMA' }
    $idSecure = ConvertTo-SecureString -String $vault.client_id_dpapi
    $secretSecure = ConvertTo-SecureString -String $vault.client_secret_dpapi
    $clientId = ([Management.Automation.PSCredential]::new('id',$idSecure)).GetNetworkCredential().Password
    $clientSecret = ([Management.Automation.PSCredential]::new('secret',$secretSecure)).GetNetworkCredential().Password
    $fingerprint = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($clientId))).ToLowerInvariant()
    if ($fingerprint -cne $observation.credentialIdSha256) { throw 'CREDENTIAL_CHANGED' }
    $profileRoot = Join-Path $root '.tradeops/b3-private-session'
    New-Item -ItemType Directory -Force -Path $profileRoot | Out-Null
    Assert-RegularPath $profileRoot
    $profilePath = Join-Path $profileRoot 'qualification-profile.dpapi.json'
    if (Test-Path -LiteralPath $profilePath) {
        $old = (Read-Encrypted $profilePath) | ConvertFrom-Json
        if ($old.expectedAccountId -ne $observation.remoteAccountId -or $old.credentialIdSha256 -cne $fingerprint) { throw 'PROFILE_CHANGED' }
    } else {
        $profilePlain = [ordered]@{schema=1; purpose='ReadOnlySessionQualification'; expectedAccountId=$observation.remoteAccountId;
            accountType=$observation.remoteAccountType; credentialIdSha256=$fingerprint; environment='Mainnet';
            independentlyVerified=$false; productionBindingActivated=$false} | ConvertTo-Json -Compress
        $secure = ConvertTo-SecureString -String $profilePlain -AsPlainText -Force
        try { $encrypted = ConvertFrom-SecureString -SecureString $secure } finally { $secure.Dispose() }
        $envelope = [ordered]@{schema=1; encryption='WindowsDPAPI-CurrentUser'; payload=$encrypted} | ConvertTo-Json -Compress
        [IO.File]::WriteAllText($profilePath,$envelope,[Text.UTF8Encoding]::new($false))
        $profilePlain = $null
    }
    $purpose = if ($WithIsolatedInbox) { 'READ_ONLY_WS_INBOX_QUALIFICATION' } else { 'READ_ONLY_WS_QUALIFICATION' }
    $payload = [ordered]@{Purpose=$purpose; ClientId=$clientId; ClientSecret=$clientSecret;
        ExpectedAccountId=$observation.remoteAccountId; AccountType=$observation.remoteAccountType; ObserveSeconds=$ObserveSeconds} | ConvertTo-Json -Compress
    if ([Text.Encoding]::UTF8.GetByteCount($payload) -gt 16384) { throw 'INPUT_TOO_LARGE' }
    $phase = 'READ_ONLY_SESSION'
    $start = [Diagnostics.ProcessStartInfo]::new()
    if ($WithIsolatedInbox) {
        $start.FileName = 'wsl.exe'
        foreach ($argument in @('-d','Ubuntu','-u','root','--','bash',$linuxRunner,'b3-live-inbox')) { $start.ArgumentList.Add($argument) }
    } else {
        $start.FileName = 'D:\DotNet\dotnet.exe'
        $start.ArgumentList.Add($dll)
    }
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $start.StandardInputEncoding = [Text.UTF8Encoding]::new($false)
    $start.Environment['DeribitTerminal__ExecutionEnabled'] = 'false'
    $start.Environment['DeribitTerminal__MainnetExecutionEnabled'] = 'false'
    $child = [Diagnostics.Process]::Start($start)
    $output = $child.StandardOutput.ReadToEndAsync(); $errorOutput = $child.StandardError.ReadToEndAsync()
    $child.StandardInput.Write($payload); $child.StandardInput.Close(); $payload = $null
    if (-not $child.WaitForExit($(if ($WithIsolatedInbox) {210000} else {110000}))) { throw 'PROBE_TIMEOUT' }
    $text = $output.GetAwaiter().GetResult()
    if ($WithIsolatedInbox) {
        $frames = @($text -split "`n" | Where-Object { $_.StartsWith('TRADEOPS_LIVE_INBOX_RESULT=',[StringComparison]::Ordinal) })
        if ($frames.Count -ne 1) { throw 'WSL_RESULT_FRAME_REQUIRED' }
        $data = $frames[0].Substring('TRADEOPS_LIVE_INBOX_RESULT='.Length) | ConvertFrom-Json
    } else { $data = $text | ConvertFrom-Json }
    $null = $errorOutput.GetAwaiter().GetResult() # never print/persist unexpected stderr
    if ($WithIsolatedInbox) {
        if ($data.Status -cnotin @('LIVE_INBOX_CAPTURE_PASS','LIVE_INBOX_CONNECTED_NO_EVENTS','LIVE_INBOX_FAILED')) { throw 'UNEXPECTED_OUTPUT' }
        if ($data.Status -cne 'LIVE_INBOX_FAILED' -and ($data.DatabaseUsed -ne $true -or $data.EnvironmentStatus -cne 'PASS' -or $data.CleanupStatus -cne 'PASS')) { throw 'INVALID_INBOX_PROOF' }
        if ($data.CaptureSourceCommit -cne $ExpectedHead) { throw 'WSL_SOURCE_CHANGED' }
    } else {
        if ($data.Status -cnotin @('LIVE_SESSION_PASS_NO_DURABLE_CAPTURE','PRIVATE_EVENT_OBSERVED_NOT_STORED','LIVE_SESSION_FAIL')) { throw 'UNEXPECTED_OUTPUT' }
        if ($data.DatabaseUsed -ne $false -or $data.DurableWrites -ne 0) { throw 'INVALID_SESSION_PROOF' }
    }
    if ($data.MutatingCalls -ne 0 -or $data.TradingReady -ne $false) { throw 'INVALID_PROOF' }
    & git -C $root diff --quiet HEAD --
    if ($LASTEXITCODE -ne 0 -or (& git -C $root rev-parse HEAD).Trim() -cne $ExpectedHead) { throw 'SOURCE_CHANGED_DURING_PROBE' }
    $data | Add-Member -NotePropertyName SourceCommit -NotePropertyValue $ExpectedHead
    $data | Add-Member -NotePropertyName ExecutableSha256 -NotePropertyValue ((Get-FileHash $dll -Algorithm SHA256).Hash.ToLowerInvariant())
    $safe = $data | ConvertTo-Json -Depth 5 -Compress
    [IO.File]::WriteAllText((Join-Path $evidence 'live-result.json'),$safe,[Text.UTF8Encoding]::new($false))
    Write-Output $safe
    if ($child.ExitCode -ne 0) { exit 1 }
} catch {
    Write-Output ([ordered]@{Status='LOCAL_PROBE_FAIL';Stage=$phase;MutatingCalls=0;DatabaseUsed=$(if ($WithIsolatedInbox) {$null} else {$false});TradingReady=$false} | ConvertTo-Json -Compress)
    exit 1
} finally {
    if ($null -ne $child) { if (-not $child.HasExited) { $child.Kill($true); $child.WaitForExit(5000) | Out-Null }; $child.Dispose() }
    foreach ($secure in @($idSecure,$secretSecure)) { if ($null -ne $secure) { $secure.Dispose() } }
    $clientId=$null; $clientSecret=$null; $payload=$null; $profilePlain=$null; $vault=$null; $observation=$null; $fingerprint=$null
}
