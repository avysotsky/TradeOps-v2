#requires -Version 7.5
[CmdletBinding()]
param([ValidateSet('Status','Run')][string]$Action='Status', [switch]$OwnerLaunch,
      [ValidatePattern('^[0-9a-f]{40}$')][string]$ExpectedHead)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
function ConvertTo-SafeBoundedOms {
    param([string]$Text,[int]$ToolExit)
    if($Text.Length -gt 16384){throw 'OUTPUT_BOUND_REFUSED'}
    $raw=$Text|ConvertFrom-Json -Depth 8
    $keys=@('Status','Stage','DatabaseUsed','PersistentStore','StagePlacementsUsed','PlacementAttempts','CancellationAttempts',
        'ReceiptsRecorded','OpenEventCommitted','CancelEventCommitted','UnexpectedFill','OwnOrderMayRemain','BaselinePositionPreserved',
        'CurrentCommandsValidated','AssessmentCompleted','RemainingCommandIssues','RecoveryBlockers','SchemaVersion','Instrument',
        'MaximumNativeUsd','MaximumStagePlacements','MinimumDistanceFraction','PostOnly','AutomaticFlatten','AccountReady','TradingReady','DurableProjectionPublished')
    if($raw -isnot [PSCustomObject] -or @($raw.PSObject.Properties).Count -ne $keys.Count -or
        @($raw.PSObject.Properties.Name|Where-Object {$_ -cnotin $keys}).Count){throw 'OUTPUT_SCHEMA_REFUSED'}
    $safe=[ordered]@{};foreach($key in $keys){$safe[$key]=$raw.$key}
    foreach($key in @('Status','Stage','Instrument')){if($safe[$key] -isnot [string]){throw 'OUTPUT_TYPE_REFUSED'}}
    foreach($key in @('MaximumNativeUsd','MinimumDistanceFraction')){
        if($safe[$key] -isnot [long] -and $safe[$key] -isnot [int] -and $safe[$key] -isnot [decimal] -and $safe[$key] -isnot [double]){throw 'OUTPUT_TYPE_REFUSED'}
    }
    foreach($key in @('DatabaseUsed','PersistentStore','OpenEventCommitted','CancelEventCommitted','UnexpectedFill','OwnOrderMayRemain',
        'BaselinePositionPreserved','CurrentCommandsValidated','AssessmentCompleted','PostOnly','AutomaticFlatten','AccountReady','TradingReady','DurableProjectionPublished')){
        if($safe[$key] -isnot [bool]){throw 'OUTPUT_TYPE_REFUSED'}
    }
    foreach($key in @('PlacementAttempts','CancellationAttempts','ReceiptsRecorded','RemainingCommandIssues','SchemaVersion','MaximumStagePlacements')){
        if(($safe[$key] -isnot [long] -and $safe[$key] -isnot [int]) -or $safe[$key] -lt 0 -or $safe[$key] -gt 10000){throw 'OUTPUT_COUNT_REFUSED'}
    }
    if($null -ne $safe.StagePlacementsUsed -and (($safe.StagePlacementsUsed -isnot [long] -and $safe.StagePlacementsUsed -isnot [int]) -or
        $safe.StagePlacementsUsed -lt 1 -or $safe.StagePlacementsUsed -gt 3)){throw 'OUTPUT_BUDGET_REFUSED'}
    if($safe.Status -cnotin @('OMS_SCENARIO_PASS','OMS_SCENARIO_SKIPPED','OMS_SCENARIO_REFUSED','OMS_SCENARIO_UNRESOLVED','OMS_SCENARIO_STOPPED_FILL') -or
        $safe.Stage -cnotin @('Input','Store','Metadata','Authentication','Capture','Preflight','Admission','Placement','OpenCapture','BeforeCancel','Cancellation','CancelCapture','TerminalRead','Recovery','Completed') -or
        $safe.SchemaVersion -ne 1 -or $safe.Instrument -cne 'ETH-PERPETUAL' -or $safe.MaximumNativeUsd -ne 1 -or
        $safe.MaximumStagePlacements -ne 3 -or $safe.MinimumDistanceFraction -ne .10 -or !$safe.PostOnly -or
        $safe.AutomaticFlatten -or $safe.AccountReady -or $safe.TradingReady -or $safe.DurableProjectionPublished -or
        $safe.PlacementAttempts -gt 1 -or $safe.CancellationAttempts -gt $safe.PlacementAttempts -or
        $safe.ReceiptsRecorded -gt ($safe.PlacementAttempts+$safe.CancellationAttempts) -or
        ($safe.PlacementAttempts -gt 0 -and $null -eq $safe.StagePlacementsUsed) -or
        ($safe.OpenEventCommitted -and $safe.PlacementAttempts -eq 0) -or ($safe.CancelEventCommitted -and $safe.CancellationAttempts -eq 0)){
        throw 'OUTPUT_AUTHORITY_REFUSED'
    }
    $allowed=@('SnapshotUnusable','SnapshotBindingInvalid','RetainedEvidenceInvalid','KnownOrderSelectionInvalid','QueryFailed','QueryBindingInvalid',
        'QueryWindowInvalid','QuerySourcesInvalid','QueryEvidenceInvalid','HistoricalUnfilledCoverageGap','OrderHistoryIncomplete','CoherentCutUnavailable',
        'BufferedReplayUnavailable','ExecutionLedgerCoverageUnproven','ExternalOrderEvidence','KnownOrderRetentionExpired','KnownOrderUnavailable',
        'KnownReadFailed','KnownReadBindingInvalid','KnownReadEvidenceInvalid','BufferedReplayInvalid','BufferedReplayDrainTimedOut',
        'AccountObservationsUnavailable','AccountReadFailed','AccountReadSkipped','AccountBindingInvalid','AccountCoverageInvalid','AccountObservationInvalid',
        'AccountObservationStale','AccountSnapshotCoherenceUnproven','AccountWideCoverageUnproven','RetainedTerminalEvidenceInsufficient','CommandLedgerInvalid','CommandRecoveryUnresolved')
    if($safe.RecoveryBlockers -isnot [array] -or $safe.RecoveryBlockers.Count -gt $allowed.Count -or
        @($safe.RecoveryBlockers|Where-Object {$_ -isnot [string] -or $_ -cnotin $allowed}).Count -or
        @($safe.RecoveryBlockers|Select-Object -Unique).Count -ne $safe.RecoveryBlockers.Count){throw 'OUTPUT_BLOCKERS_REFUSED'}
    if($safe.Status -ceq 'OMS_SCENARIO_PASS'){
        if($ToolExit -ne 0 -or $safe.Stage -cne 'Completed' -or !$safe.DatabaseUsed -or !$safe.PersistentStore -or
            $safe.PlacementAttempts -ne 1 -or $safe.CancellationAttempts -ne 1 -or $safe.ReceiptsRecorded -ne 2 -or
            !$safe.OpenEventCommitted -or !$safe.CancelEventCommitted -or $safe.UnexpectedFill -or $safe.OwnOrderMayRemain -or
            !$safe.BaselinePositionPreserved -or !$safe.CurrentCommandsValidated -or !$safe.AssessmentCompleted -or
            @('CoherentCutUnavailable','BufferedReplayUnavailable','ExecutionLedgerCoverageUnproven'|Where-Object {$_ -cnotin $safe.RecoveryBlockers}).Count){throw 'OUTPUT_PASS_REFUSED'}
    }elseif($safe.Status -ceq 'OMS_SCENARIO_SKIPPED'){
        if($ToolExit -ne 0 -or $safe.Stage -cnotin @('Metadata','Preflight') -or $safe.PlacementAttempts -or $safe.CancellationAttempts -or
            $safe.ReceiptsRecorded -or $safe.UnexpectedFill -or $safe.OwnOrderMayRemain -or $null -ne $safe.StagePlacementsUsed){throw 'OUTPUT_SKIP_REFUSED'}
    }else{
        if($ToolExit -eq 0 -or ($safe.Status -ceq 'OMS_SCENARIO_STOPPED_FILL') -ne $safe.UnexpectedFill -or
            ($safe.Status -ceq 'OMS_SCENARIO_REFUSED' -and $safe.PlacementAttempts -ne 0) -or
            ($safe.Status -cne 'OMS_SCENARIO_REFUSED' -and $safe.PlacementAttempts -ne 1)){throw 'OUTPUT_FAILURE_REFUSED'}
    }
    return $safe
}
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if($Action -eq 'Status'){
    [ordered]@{Mode='OwnerLaunchOnly';StageId='B4-bounded-oms-v1';Instrument='ETH-PERPETUAL';MaximumNativeUsd=1;
        MaximumStagePlacements=3;MinimumDistanceFraction=.10;PostOnly=$true;RequiresQualifiedExistingStore=$true;
        PlacementAttempts=0;CancellationAttempts=0;TradingReady=$false}|ConvertTo-Json -Compress
    return
}
if(!$OwnerLaunch -or !$ExpectedHead){throw 'EXPLICIT_OWNER_LAUNCH_AND_SOURCE_REQUIRED'}
function Assert-Source {
    if((& git -C $repo rev-parse HEAD).Trim() -cne $ExpectedHead -or $LASTEXITCODE -ne 0){throw 'SOURCE_HEAD_REFUSED'}
    & git -C $repo diff --quiet HEAD --
    if($LASTEXITCODE -ne 0){throw 'COMMITTED_SOURCE_REQUIRED'}
    $other=@(& git -C $repo ls-files --others --exclude-standard|Where-Object {$_ -cne 'src/TradeOps.Api/Properties/launchSettings.json'})
    if($LASTEXITCODE -ne 0 -or $other.Count){throw 'UNTRACKED_SOURCE_REFUSED'}
}
Assert-Source
$previous=$env:DOTNET_CLI_USE_MSBUILD_SERVER
try{
    $env:DOTNET_CLI_USE_MSBUILD_SERVER='0'
    $build=Join-Path $repo '.tradeops/bounded-oms-build.log'
    & D:\DotNet\dotnet.exe build (Join-Path $repo 'tools/TradeOps.DeribitMainnetEthQualification/TradeOps.DeribitMainnetEthQualification.csproj') -c Release --no-restore -m:1 -nr:false -p:BuildInParallel=false -p:UseSharedCompilation=false *> $build
    if($LASTEXITCODE -ne 0){throw 'BUILD_REFUSED'}
}finally{$env:DOTNET_CLI_USE_MSBUILD_SERVER=$previous}
$bin=Join-Path $repo 'tools/TradeOps.DeribitMainnetEthQualification/bin/Release/net10.0'
$hashes=@{};Get-ChildItem -LiteralPath $bin -Filter 'TradeOps.*.dll'|ForEach-Object {$hashes[$_.FullName]=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
$idSecure=$null;$secretSecure=$null;$pinSecure=$null;$payload=$null;$process=$null
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
    if($pin.environment -cne 'Mainnet' -or $pin.expectedAccountId -le 0 -or $pin.accountType -cnotin @('main','subaccount') -or $pin.credentialIdSha256 -cne $fingerprint){throw 'NATIVE_PIN_REFUSED'}
    $payload=@{Purpose='OWNER_BOUNDED_MAINNET_OMS_V1';Environment='Mainnet';OwnerLaunched=$true;StageId='B4-bounded-oms-v1';
        ExpectedAccountId=$pin.expectedAccountId;AccountType=$pin.accountType;ClientId=$id;ClientSecret=$secret}|ConvertTo-Json -Compress
    Assert-Source
    $process=[Diagnostics.Process]::new();$process.StartInfo=[Diagnostics.ProcessStartInfo]::new('wsl.exe')
    foreach($arg in @('-d','Ubuntu','-u','root','--','bash','/mnt/d/Projects/TradeOps-v2/scripts/testing/mainnet-bounded-oms.sh','owner-run')){$process.StartInfo.ArgumentList.Add($arg)}
    $process.StartInfo.UseShellExecute=$false;$process.StartInfo.CreateNoWindow=$true
    $process.StartInfo.RedirectStandardInput=$true;$process.StartInfo.RedirectStandardOutput=$true;$process.StartInfo.RedirectStandardError=$true
    if(!$process.Start()){throw 'OWNER_RUN_START_REFUSED'}
    $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
    $process.StandardInput.WriteLine($payload);$process.StandardInput.Close()
    $id=$null;$secret=$null;$pin=$null;$payload=$null;$saved=$null
    if(!$process.WaitForExit(240000)){throw 'OWNER_RUN_STILL_RUNNING_DO_NOT_REPEAT_REVIEW_JOURNAL'}
    $null=$stderr.GetAwaiter().GetResult();$raw=$stdout.GetAwaiter().GetResult()
    if($raw.Length -gt 32768){throw 'OWNER_OUTPUT_BOUND_REFUSED'}
    $frames=@($raw -split '\r?\n'|Where-Object {$_.StartsWith('{')})
    $evidence=@($raw -split '\r?\n'|Where-Object {$_ -match '^BOUNDED_OMS_EVIDENCE=\.tradeops/bounded-oms/[A-Za-z0-9_-]+ EXIT=[01]$'})
    if($frames.Count -ne 1 -or $evidence.Count -ne 1){throw 'OWNER_ENVIRONMENT_EVIDENCE_REFUSED'}
    $safe=ConvertTo-SafeBoundedOms $frames[0] $process.ExitCode
    if($safe.Status -ceq 'OMS_SCENARIO_PASS' -and $evidence[0] -notmatch ' EXIT=0$'){throw 'OWNER_ENVIRONMENT_FAILED'}
    Assert-Source
    foreach($path in $hashes.Keys){if((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne $hashes[$path]){throw 'ASSEMBLY_CHANGED'}}
    $safe['SourceCommit']=$ExpectedHead;$safe['EnvironmentEvidence']=$evidence[0]
    $summary=Join-Path $repo ('.tradeops/bounded-oms/owner-summary-'+[DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmss')+'-'+[Guid]::NewGuid().ToString('N')+'.json')
    [IO.File]::WriteAllText($summary,($safe|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
    $safe|ConvertTo-Json -Depth 8 -Compress
    if($process.ExitCode -ne 0){throw 'OWNER_SCENARIO_NOT_PASSED_DO_NOT_REPEAT_REVIEW_JOURNAL'}
}finally{
    $id=$null;$secret=$null;$pin=$null;$payload=$null;$saved=$null;$raw=$null
    foreach($secure in @($idSecure,$secretSecure,$pinSecure)){if($secure){$secure.Dispose()}}
    if($process -and $process.HasExited){$process.Dispose()}
}
