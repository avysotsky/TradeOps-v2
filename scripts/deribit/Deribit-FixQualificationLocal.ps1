#requires -Version 7.5
[CmdletBinding()]
param([ValidateSet('Status','Run')][string]$Action='Status',[switch]$OwnerLaunch,
      [ValidatePattern('^[0-9a-f]{40}$')][string]$ExpectedHead)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if($Action -eq 'Status'){
    [ordered]@{Mode='OwnerLaunchOnly';Stage='owner-fix-eth-qualification-v1';Instrument='ETH-PERPETUAL';
        MaximumNativeUsd=1;MaximumPlacements=3;MinimumDistanceFraction=.10;PostOnly=$true;
        Operations=@('Place','PriceOnlyNativeReplace','AddressedCancel');VaultUsed=$false;TradingStarted=$false}|ConvertTo-Json -Compress
    return
}
if(!$OwnerLaunch -or !$ExpectedHead){throw 'EXPLICIT_OWNER_LAUNCH_AND_SOURCE_REQUIRED'}
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
function Assert-Source {
    if((& git -C $repo rev-parse HEAD).Trim() -cne $ExpectedHead -or $LASTEXITCODE -ne 0){throw 'SOURCE_HEAD_REFUSED'}
    & git -C $repo diff --quiet HEAD --
    if($LASTEXITCODE -ne 0){throw 'COMMITTED_SOURCE_REQUIRED'}
    $untracked=@(& git -C $repo ls-files --others --exclude-standard|Where-Object {$_ -cne 'src/TradeOps.Api/Properties/launchSettings.json'})
    if($LASTEXITCODE -ne 0 -or $untracked.Count){throw 'UNTRACKED_SOURCE_REFUSED'}
}
Assert-Source
foreach($key in @('ConnectionStrings__TradeOpsCaptureDb','ConnectionStrings__TradeOpsRecoveryDb','ConnectionStrings__TradeOpsExecutionDb',
    'ConnectionStrings__TradeOpsFixDb','DeribitOms__LockRoot','DeribitFix__SenderCompId','DeribitCapture__Tenant','DeribitCapture__Account','DeribitRecovery__InitialFromUtc')){
    if([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($key))){throw 'PROVISIONED_HOST_CONFIGURATION_REQUIRED'}
}
$previous=$env:DOTNET_CLI_USE_MSBUILD_SERVER
try{
    $env:DOTNET_CLI_USE_MSBUILD_SERVER='0'
    $log=Join-Path $repo '.tradeops/fix-qualification-build.log'
    & D:\DotNet\dotnet.exe build (Join-Path $repo 'src/TradeOps.Worker/TradeOps.Worker.csproj') -c Release --no-restore -m:1 -nr:false -p:BuildInParallel=false -p:UseSharedCompilation=false *> $log
    if($LASTEXITCODE -ne 0){throw 'BUILD_REFUSED'}
}finally{$env:DOTNET_CLI_USE_MSBUILD_SERVER=$previous}
$bin=Join-Path $repo 'src/TradeOps.Worker/bin/Release/net10.0'
$hashes=@{};foreach($file in Get-ChildItem -LiteralPath $bin -File -Filter '*.dll'){$hashes[$file.FullName]=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash}
$idSecure=$null;$secretSecure=$null;$pinSecure=$null;$process=$null;$started=$false
try{
    $vault=Join-Path $env:LOCALAPPDATA 'TradeOps/Credentials/Deribit.Mainnet.ReadOnly.dpapi.json'
    $pinPath=Join-Path $repo '.tradeops/b3-private-session/qualification-profile.dpapi.json'
    foreach($path in @($vault,$pinPath)){if((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'REPARSE_REFUSED'}}
    $saved=Get-Content -LiteralPath $vault -Raw|ConvertFrom-Json
    $envelope=Get-Content -LiteralPath $pinPath -Raw|ConvertFrom-Json
    if($saved.schema -ne 1 -or $saved.environment -cne 'DeribitMainnetReadOnly' -or $envelope.schema -ne 1 -or $envelope.encryption -cne 'WindowsDPAPI-CurrentUser'){throw 'BINDING_FORMAT_REFUSED'}
    $idSecure=ConvertTo-SecureString $saved.client_id_dpapi;$secretSecure=ConvertTo-SecureString $saved.client_secret_dpapi;$pinSecure=ConvertTo-SecureString $envelope.payload
    $id=([Management.Automation.PSCredential]::new('local',$idSecure)).GetNetworkCredential().Password
    $secret=([Management.Automation.PSCredential]::new('local',$secretSecure)).GetNetworkCredential().Password
    $pin=([Management.Automation.PSCredential]::new('local',$pinSecure)).GetNetworkCredential().Password|ConvertFrom-Json
    $fingerprint=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($id))).ToLowerInvariant()
    if($pin.environment -cne 'Mainnet' -or $pin.expectedAccountId -le 0 -or $pin.accountType -cne 'main' -or $pin.credentialIdSha256 -cne $fingerprint){throw 'NATIVE_PIN_REFUSED'}
    Assert-Source
    $process=[Diagnostics.Process]::new();$process.StartInfo=[Diagnostics.ProcessStartInfo]::new('D:\DotNet\dotnet.exe')
    $process.StartInfo.ArgumentList.Add((Join-Path $bin 'TradeOps.Worker.dll'))
    $process.StartInfo.WorkingDirectory=Join-Path $repo 'src/TradeOps.Worker'
    $process.StartInfo.UseShellExecute=$false;$process.StartInfo.CreateNoWindow=$true
    $process.StartInfo.RedirectStandardOutput=$true;$process.StartInfo.RedirectStandardError=$true
    $child=@{Worker__Mode='DeribitFixOwnerQualification';DeribitOms__Enabled='true';DeribitFix__Enabled='true';
        DeribitFixQualification__OwnerLaunch='true';DeribitFixQualification__ExpectedSourceCommit=$ExpectedHead;DeribitFixQualification__ObservedSourceCommit=$ExpectedHead;
        DeribitCapture__Environment='Mainnet';DeribitCapture__ExpectedAccountId=[string]$pin.expectedAccountId;DeribitCapture__ExpectedAccountType='main';
        DeribitCapture__Instruments__0__Name='ETH-PERPETUAL';DeribitCapture__Instruments__0__Kind='future';DeribitCapture__Instruments__0__Currency='ETH';
        DeribitCapture__Instruments__0__AmountUnit='Usd';DeribitCapture__Instruments__0__AmountCurrency='USD';
        DeribitCapture__ClientId=$id;DeribitCapture__ClientSecret=$secret;DeribitTerminal__ExecutionEnabled='false';DeribitTerminal__MainnetExecutionEnabled='false'}
    foreach($key in $child.Keys){$process.StartInfo.Environment[$key]=$child[$key]}
    if(!$process.Start()){throw 'OWNER_RUN_START_REFUSED'}
    $started=$true
    $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
    $id=$null;$secret=$null;$pin=$null;$saved=$null;$child=$null
    if(!$process.WaitForExit(240000)){throw 'OWNER_RUN_STILL_RUNNING_DO_NOT_REPEAT_REVIEW_JOURNAL'}
    $null=$stderr.GetAwaiter().GetResult();$raw=$stdout.GetAwaiter().GetResult()
    if($raw.Length -gt 65536){throw 'OWNER_OUTPUT_BOUND_REFUSED'}
    $frames=@($raw -split '\r?\n'|Where-Object {$_.StartsWith('{')})
    if($frames.Count -ne 1){throw 'OWNER_OUTPUT_FRAME_REFUSED'}
    $result=$frames[0]|ConvertFrom-Json -Depth 8
    $keys=@('Status','Placed','Amended','Cancelled','TerminalRecovered','UnexpectedFill','OwnOrderMayRemain','PositionsPreserved','StagePlacementsUsedUpperBound')
    if($result -isnot [PSCustomObject] -or @($result.PSObject.Properties).Count -ne $keys.Count -or @($result.PSObject.Properties.Name|Where-Object {$_ -cnotin $keys}).Count){throw 'OWNER_OUTPUT_SCHEMA_REFUSED'}
    foreach($key in @('Placed','Amended','Cancelled','TerminalRecovered','OwnOrderMayRemain','PositionsPreserved')){if($result.$key -isnot [bool]){throw 'OWNER_OUTPUT_TYPE_REFUSED'}}
    if($null -ne $result.UnexpectedFill -and $result.UnexpectedFill -isnot [bool]){throw 'OWNER_OUTPUT_TYPE_REFUSED'}
    if($result.StagePlacementsUsedUpperBound -isnot [long] -and $result.StagePlacementsUsedUpperBound -isnot [int]){throw 'OWNER_OUTPUT_TYPE_REFUSED'}
    if($result.StagePlacementsUsedUpperBound -lt 0 -or $result.StagePlacementsUsedUpperBound -gt 3){throw 'OWNER_OUTPUT_BUDGET_REFUSED'}
    if($result.Status -cnotin @('FIX_QUALIFICATION_SCENARIO_PASS','FIX_QUALIFICATION_REFUSED','FIX_QUALIFICATION_BUDGET_EXHAUSTED',
        'FIX_QUALIFICATION_STOPPED_UNKNOWN','FIX_QUALIFICATION_STOPPED_FILL','FIX_QUALIFICATION_STOPPED_EVIDENCE','FIX_QUALIFICATION_STOPPED_TIMEOUT')){throw 'OWNER_OUTPUT_STATUS_REFUSED'}
    if($result.Status -ceq 'FIX_QUALIFICATION_SCENARIO_PASS' -and ($process.ExitCode -ne 0 -or !$result.Placed -or !$result.Amended -or !$result.Cancelled -or
        !$result.TerminalRecovered -or !$result.PositionsPreserved -or $result.OwnOrderMayRemain -or $result.UnexpectedFill -cne $false -or $result.StagePlacementsUsedUpperBound -lt 1)){throw 'OWNER_PASS_REFUSED'}
    if($result.Status -cne 'FIX_QUALIFICATION_SCENARIO_PASS' -and $process.ExitCode -eq 0){throw 'OWNER_FAILURE_EXIT_REFUSED'}
    Assert-Source
    foreach($path in $hashes.Keys){if((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne $hashes[$path]){throw 'ASSEMBLY_CHANGED'}}
    $directory=Join-Path $repo '.tradeops/fix-qualification';$null=New-Item -ItemType Directory -Force -Path $directory
    $summary=Join-Path $directory ('owner-summary-'+[DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmss')+'-'+[Guid]::NewGuid().ToString('N')+'.json')
    [IO.File]::WriteAllText($summary,($result|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
    $result|ConvertTo-Json -Compress
    if($process.ExitCode -ne 0){throw 'OWNER_SCENARIO_NOT_PASSED_DO_NOT_REPEAT_REVIEW_JOURNAL'}
}finally{
    $id=$null;$secret=$null;$pin=$null;$saved=$null;$child=$null;$raw=$null
    foreach($secure in @($idSecure,$secretSecure,$pinSecure)){if($secure){$secure.Dispose()}}
    if($process -and (!$started -or $process.HasExited)){$process.Dispose()}
}
