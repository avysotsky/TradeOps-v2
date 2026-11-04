#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Status','StoreCheck','Run')][string]$Action='Status',
    [ValidatePattern('^[0-9a-f]{40}$')][string]$ExpectedHead
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$local=Join-Path $repo '.tradeops/b3-mainnet-small-account'
$vault=Join-Path $env:LOCALAPPDATA 'TradeOps/Credentials/Deribit.Mainnet.ReadOnly.dpapi.json'
$pinPath=Join-Path $repo '.tradeops/b3-private-session/qualification-profile.dpapi.json'
if($Action -eq 'Status'){
    [ordered]@{Environment='Mainnet';Instrument='ETH-PERPETUAL';MaximumNativeUsd=1;
        ExistingCredentialPresent=(Test-Path -LiteralPath $vault);ExistingAccountPinPresent=(Test-Path -LiteralPath $pinPath);
        PreviousAttemptPresent=(Test-Path -LiteralPath (Join-Path $local 'attempt/attempt.json'));MutatingCalls=0}|ConvertTo-Json -Compress
    return
}
if(!$ExpectedHead){throw 'EXACT_SOURCE_HEAD_REQUIRED'}
if([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT){throw 'WINDOWS_REQUIRED'}
function Assert-Source {
    $head=(& git -C $repo rev-parse HEAD).Trim()
    if($LASTEXITCODE -ne 0 -or $head -cne $ExpectedHead){throw 'SOURCE_SHA_MISMATCH'}
    & git -C $repo diff --quiet HEAD --
    if($LASTEXITCODE -ne 0){throw 'COMMITTED_SOURCE_REQUIRED'}
    $untracked=@(& git -C $repo ls-files --others --exclude-standard)
    if($LASTEXITCODE -ne 0 -or @($untracked|Where-Object {$_ -cne 'src/TradeOps.Api/Properties/launchSettings.json'}).Count -ne 0){throw 'UNTRACKED_SOURCE_REFUSED'}
}
function Assert-Regular([string]$path){
    if((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'REPARSE_POINT_REFUSED'}
}
Assert-Source
New-Item -ItemType Directory -Force -Path $local|Out-Null
foreach($path in @($repo,(Join-Path $repo '.tradeops'),$local)){Assert-Regular $path}
if($Action -eq 'Run' -and (Test-Path -LiteralPath (Join-Path $local 'attempt/attempt.json'))){throw 'EXISTING_ATTEMPT_REQUIRES_RECONCILIATION; NO_REPEAT'}
$previous=$env:DOTNET_CLI_USE_MSBUILD_SERVER
try{
    $env:DOTNET_CLI_USE_MSBUILD_SERVER='0'
    & D:\DotNet\dotnet.exe build (Join-Path $repo 'tools/TradeOps.DeribitMainnetEthQualification/TradeOps.DeribitMainnetEthQualification.csproj') `
        -c Release --no-restore -m:1 -nr:false -p:BuildInParallel=false -p:UseSharedCompilation=false *> (Join-Path $local 'build.log')
    if($LASTEXITCODE -ne 0){throw 'MAINNET_BUILD_FAILED'}
}finally{$env:DOTNET_CLI_USE_MSBUILD_SERVER=$previous}
$runner=(& wsl.exe -d Ubuntu --exec wslpath -a (Join-Path $repo 'scripts/testing/mainnet-qualification.sh')).Trim()
if($LASTEXITCODE -ne 0 -or $runner -notmatch '^/[A-Za-z0-9_./-]+$'){throw 'RUNNER_PATH_REFUSED'}
$mode=if($Action -eq 'StoreCheck'){'store-check'}else{'run'}
$process=[Diagnostics.Process]::new()
$process.StartInfo=[Diagnostics.ProcessStartInfo]::new('wsl.exe',('-d Ubuntu -u root -- bash '+$runner+' '+$mode))
$process.StartInfo.UseShellExecute=$false;$process.StartInfo.CreateNoWindow=$true
$process.StartInfo.RedirectStandardInput=$true;$process.StartInfo.RedirectStandardOutput=$true;$process.StartInfo.RedirectStandardError=$true
$idSecure=$null;$secretSecure=$null;$payload=$null;$id=$null;$secret=$null;$pin=$null
try{
    if($Action -eq 'Run'){
        foreach($path in @($vault,$pinPath)){Assert-Regular $path}
        $saved=Get-Content -LiteralPath $vault -Raw|ConvertFrom-Json
        if($saved.schema -ne 1 -or $saved.environment -cne 'DeribitMainnetReadOnly'){throw 'EXISTING_VAULT_SCHEMA_MISMATCH'}
        $idSecure=ConvertTo-SecureString $saved.client_id_dpapi;$secretSecure=ConvertTo-SecureString $saved.client_secret_dpapi
        $id=([Management.Automation.PSCredential]::new('local',$idSecure)).GetNetworkCredential().Password
        $secret=([Management.Automation.PSCredential]::new('local',$secretSecure)).GetNetworkCredential().Password
        $envelope=Get-Content -LiteralPath $pinPath -Raw|ConvertFrom-Json
        if($envelope.schema -ne 1 -or $envelope.encryption -cne 'WindowsDPAPI-CurrentUser'){throw 'PIN_FORMAT'}
        $pinSecure=ConvertTo-SecureString $envelope.payload
        try{$pin=([Management.Automation.PSCredential]::new('local',$pinSecure)).GetNetworkCredential().Password|ConvertFrom-Json}finally{$pinSecure.Dispose()}
        $fingerprint=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($id))).ToLowerInvariant()
        if($pin.environment -cne 'Mainnet' -or $pin.expectedAccountId -le 0 -or $pin.accountType -cnotin @('main','subaccount') -or $pin.credentialIdSha256 -cne $fingerprint){throw 'SAVED_ACCOUNT_PIN_MISMATCH'}
        $payload=[ordered]@{Purpose='MAINNET_SMALL_ETH_ONE_ORDER_V1';Environment='Mainnet';ApprovedOneOrderAndCancellation=$true;
            ExpectedAccountId=$pin.expectedAccountId;AccountType=$pin.accountType;ClientId=$id;ClientSecret=$secret}|ConvertTo-Json -Compress
    }
    if(!$process.Start()){throw 'MAINNET_CHILD_START_FAILED'}
    $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
    if($payload){$process.StandardInput.WriteLine($payload)}
    $process.StandardInput.Close()
}finally{
    $id=$null;$secret=$null;$payload=$null;$saved=$null;$pin=$null
    if($idSecure){$idSecure.Dispose()};if($secretSecure){$secretSecure.Dispose()}
}
if(!$process.WaitForExit(240000)){throw 'MAINNET_CHILD_STILL_RUNNING; DO_NOT_REPEAT; REVIEW_ATTEMPT_MARKER'}
$lines=@($stdout.GetAwaiter().GetResult() -split '\r?\n'|Where-Object {$_.StartsWith('TRADEOPS_MAINNET_ETH_RESULT=')})
$null=$stderr.GetAwaiter().GetResult()
if($lines.Count -ne 1){throw 'MAINNET_RESULT_UNKNOWN; REVIEW_PERSISTENT_JOURNAL_AND_MARKER'}
$result=$lines[0].Substring('TRADEOPS_MAINNET_ETH_RESULT='.Length)|ConvertFrom-Json
Assert-Source
$result|Add-Member -NotePropertyName SourceCommit -NotePropertyValue $ExpectedHead
[IO.File]::WriteAllText((Join-Path $local $(if($Action -eq 'Run'){'live-result.json'}else{'store-result.json'})),($result|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
$result|ConvertTo-Json -Depth 8 -Compress
$expectedStatus=if($Action -eq 'Run'){'MAINNET_ETH_CAPTURE_PASS'}else{'MAINNET_STORE_READY'}
if($process.ExitCode -ne 0 -or $result.Status -cne $expectedStatus -or $result.EnvironmentStatus -cne 'PASS' -or $result.CaptureSourceCommit -cne $ExpectedHead){throw 'MAINNET_QUALIFICATION_NOT_PASSED; NO_AUTOMATIC_REPEAT'}
