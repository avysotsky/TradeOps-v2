#requires -Version 7.5
[CmdletBinding()]
param([DateTimeOffset]$FromUtc=[DateTimeOffset]::UtcNow.AddDays(-1),
      [DateTimeOffset]$ThroughUtc=[DateTimeOffset]::UtcNow.AddSeconds(-5))
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
function ConvertTo-SafeAccountRecovery {
    param([string]$rawText,[int]$toolExit,[DateTimeOffset]$invoked)
    if($rawText.Length -gt 16384){throw 'DIAGNOSTICS_BOUND_REFUSED'}
    $raw=$rawText|ConvertFrom-Json -Depth 8 -DateKind String
    $keys=@('Status','Stage','MetadataValidated','DatabaseUsed','SelectOnly','AssessmentCompleted','RequestedInstruments',
        'ScopedAccountObservationsValidated','AcceptedPositionRows','BalanceObserved','RetainedFacts','EnumeratedFacts','KnownOrders',
        'AddressedOrders','RetentionExpiredOrders','FromUtc','ThroughUtc','AssessedAtUtc','AccountProblems','Blockers','SchemaVersion',
        'OrderInstrument','ManifestPolicy','Currency','Kind','AccountWideCoverage','VenueCoherent','AccountReady','DurableProjectionPublished',
        'TradingReady','DurableWrites','MutatingCalls','RetainedTerminalOrdersValidated')
    if($raw -isnot [PSCustomObject] -or @($raw.PSObject.Properties).Count -ne $keys.Count -or
        @($raw.PSObject.Properties.Name|Where-Object {$_ -cnotin $keys}).Count){throw 'DIAGNOSTICS_SCHEMA_REFUSED'}
    $safe=[ordered]@{};foreach($key in $keys){$safe[$key]=$raw.$key}
    foreach($key in @('Status','Stage','OrderInstrument','ManifestPolicy','Currency','Kind')){
        if($safe[$key] -isnot [string]){throw 'DIAGNOSTICS_TYPE_REFUSED'}
    }
    foreach($key in @('RequestedInstruments','AcceptedPositionRows','RetainedFacts','EnumeratedFacts','KnownOrders','AddressedOrders',
        'RetentionExpiredOrders','SchemaVersion','DurableWrites','MutatingCalls','RetainedTerminalOrdersValidated')){
        if($safe[$key] -isnot [long] -and $safe[$key] -isnot [int]){throw 'DIAGNOSTICS_TYPE_REFUSED'}
        if($safe[$key] -lt 0 -or $safe[$key] -gt 10000){throw 'DIAGNOSTICS_COUNT_REFUSED'}
    }
    foreach($key in @('MetadataValidated','DatabaseUsed','SelectOnly','AssessmentCompleted','ScopedAccountObservationsValidated',
        'BalanceObserved','AccountWideCoverage','VenueCoherent','AccountReady','DurableProjectionPublished','TradingReady')){
        if($safe[$key] -isnot [bool]){throw 'DIAGNOSTICS_TYPE_REFUSED'}
    }
    if($safe.SchemaVersion -ne 2 -or $safe.OrderInstrument -cne 'ETH-PERPETUAL' -or $safe.ManifestPolicy -cne 'EthActiveInverseFuturesV1' -or
        $safe.Currency -cne 'ETH' -or $safe.Kind -cne 'future' -or $safe.RequestedInstruments -gt 256 -or
        $safe.AcceptedPositionRows -gt $safe.RequestedInstruments -or $safe.AddressedOrders -gt 32 -or
        $safe.RetainedTerminalOrdersValidated -gt $safe.RetentionExpiredOrders -or
        $safe.AddressedOrders+$safe.RetentionExpiredOrders -gt $safe.KnownOrders -or
        $safe.AccountWideCoverage -or $safe.VenueCoherent -or $safe.AccountReady -or $safe.DurableProjectionPublished -or
        $safe.TradingReady -or $safe.DurableWrites -ne 0 -or $safe.MutatingCalls -ne 0){throw 'DIAGNOSTICS_AUTHORITY_REFUSED'}
    if($safe.AssessedAtUtc -isnot [string] -or $safe.AssessedAtUtc.Length -gt 64){throw 'DIAGNOSTICS_TIME_REFUSED'}
    $at=[DateTimeOffset]::Parse($safe.AssessedAtUtc,[Globalization.CultureInfo]::InvariantCulture)
    if($at.Offset -ne [TimeSpan]::Zero -or $at -lt $invoked.AddSeconds(-1) -or $at -gt [DateTimeOffset]::UtcNow.AddSeconds(5)){throw 'DIAGNOSTICS_TIME_REFUSED'}
    $accountProblems=@('ReadSkipped','ReadFailed','BindingInvalid','CoverageInvalid','ObservationInvalid','StaleObservation')
    $blockers=@('SnapshotUnusable','SnapshotBindingInvalid','RetainedEvidenceInvalid','KnownOrderSelectionInvalid','QueryFailed',
        'QueryBindingInvalid','QueryWindowInvalid','QuerySourcesInvalid','QueryEvidenceInvalid','HistoricalUnfilledCoverageGap',
        'OrderHistoryIncomplete','CoherentCutUnavailable','BufferedReplayUnavailable','ExecutionLedgerCoverageUnproven','ExternalOrderEvidence',
        'KnownOrderRetentionExpired','KnownOrderUnavailable','KnownReadFailed','KnownReadBindingInvalid','KnownReadEvidenceInvalid',
        'BufferedReplayInvalid','BufferedReplayDrainTimedOut','AccountObservationsUnavailable','AccountReadFailed','AccountReadSkipped',
        'AccountBindingInvalid','AccountCoverageInvalid','AccountObservationInvalid','AccountObservationStale',
        'AccountSnapshotCoherenceUnproven','AccountWideCoverageUnproven','RetainedTerminalEvidenceInsufficient')
    foreach($key in @('AccountProblems','Blockers')){
        $allowed=if($key -ceq 'AccountProblems'){$accountProblems}else{$blockers}
        if($safe[$key] -isnot [array] -or @($safe[$key]).Count -gt $allowed.Count -or
            @($safe[$key]|Where-Object {$_ -isnot [string] -or $_ -cnotin $allowed}).Count -or
            @($safe[$key]|Select-Object -Unique).Count -ne @($safe[$key]).Count){throw 'DIAGNOSTICS_PROBLEMS_REFUSED'}
    }
    if($safe.MetadataValidated -ne ($safe.RequestedInstruments -gt 0) -or $safe.SelectOnly -and !$safe.DatabaseUsed){throw 'DIAGNOSTICS_BINDING_REFUSED'}
    if($safe.Stage -ceq 'Input'){
        if($null -ne $safe.FromUtc -or $null -ne $safe.ThroughUtc){throw 'DIAGNOSTICS_TIME_REFUSED'}
    }else{
        foreach($key in @('FromUtc','ThroughUtc')){if($safe[$key] -isnot [string] -or $safe[$key].Length -gt 64){throw 'DIAGNOSTICS_TIME_REFUSED'}}
        $from=[DateTimeOffset]::Parse($safe.FromUtc,[Globalization.CultureInfo]::InvariantCulture)
        $through=[DateTimeOffset]::Parse($safe.ThroughUtc,[Globalization.CultureInfo]::InvariantCulture)
        if($from.Offset -ne [TimeSpan]::Zero -or $through.Offset -ne [TimeSpan]::Zero -or $from -le [DateTimeOffset]::UnixEpoch.AddHours(1) -or
            $through -lt $from -or $through -gt $at -or $through-$from -gt [TimeSpan]::FromDays(7)){throw 'DIAGNOSTICS_TIME_REFUSED'}
    }
    if($safe.AssessmentCompleted){
        if($toolExit -ne 0 -or $safe.Status -cne 'RECOVERY_ACCOUNT_EVIDENCE_BLOCKED' -or $safe.Stage -cne 'Completed' -or
            !$safe.MetadataValidated -or !$safe.DatabaseUsed -or !$safe.SelectOnly){throw 'DIAGNOSTICS_COMPLETION_REFUSED'}
        foreach($required in @('CoherentCutUnavailable','BufferedReplayUnavailable','ExecutionLedgerCoverageUnproven','AccountSnapshotCoherenceUnproven','AccountWideCoverageUnproven')){
            if($required -cnotin $safe.Blockers){throw 'DIAGNOSTICS_GATES_REFUSED'}
        }
        if($safe.RetentionExpiredOrders -gt $safe.RetainedTerminalOrdersValidated){
            foreach($required in @('KnownOrderRetentionExpired','KnownOrderUnavailable','RetainedTerminalEvidenceInsufficient')){
                if($required -cnotin $safe.Blockers){throw 'DIAGNOSTICS_TERMINAL_REFUSED'}
            }
        }elseif('KnownOrderRetentionExpired' -cin $safe.Blockers -or 'RetainedTerminalEvidenceInsufficient' -cin $safe.Blockers){throw 'DIAGNOSTICS_TERMINAL_REFUSED'}
        if($safe.ScopedAccountObservationsValidated){
            if(!$safe.BalanceObserved -or $safe.AccountProblems.Count -or 'AccountObservationsUnavailable' -cin $safe.Blockers){throw 'DIAGNOSTICS_ACCOUNT_REFUSED'}
        }elseif($safe.BalanceObserved -or $safe.AcceptedPositionRows -ne 0 -or !$safe.AccountProblems.Count -or
            'AccountObservationsUnavailable' -cnotin $safe.Blockers){throw 'DIAGNOSTICS_ACCOUNT_REFUSED'}
    }else{
        if($toolExit -eq 0 -or $safe.Status -cne 'RECOVERY_ACCOUNT_ASSESSMENT_REFUSED' -or $safe.Stage -cnotin @('Input','Metadata','Store','Assessment') -or
            $safe.ScopedAccountObservationsValidated -or $safe.BalanceObserved -or $safe.AcceptedPositionRows -or $safe.RetainedFacts -or
            $safe.EnumeratedFacts -or $safe.KnownOrders -or $safe.AddressedOrders -or $safe.RetentionExpiredOrders -or $safe.RetainedTerminalOrdersValidated -or
            $safe.AccountProblems.Count -or $safe.Blockers.Count){throw 'DIAGNOSTICS_REFUSAL_REFUSED'}
    }
    return $safe
}
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$head=(& git -C $repo rev-parse HEAD).Trim()
if($LASTEXITCODE -ne 0){throw 'SOURCE_HEAD_REQUIRED'}
& git -C $repo diff --quiet HEAD --
if($LASTEXITCODE -ne 0){throw 'COMMITTED_SOURCE_REQUIRED'}
$untracked=@(& git -C $repo ls-files --others --exclude-standard)
if($LASTEXITCODE -ne 0 -or @($untracked|Where-Object {$_ -cne 'src/TradeOps.Api/Properties/launchSettings.json'}).Count){throw 'UNTRACKED_SOURCE_REFUSED'}
$out=Join-Path $repo ('.tradeops/account-recovery/'+[DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmss')+'-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $out -Force|Out-Null
$assembly=Join-Path $repo 'tools/TradeOps.DeribitMainnetEthQualification/bin/Release/net10.0/TradeOps.DeribitMainnetEthQualification.dll'
$hashes=[ordered]@{}
foreach($name in @('TradeOps.DeribitMainnetEthQualification','TradeOps.Connectors.Deribit','TradeOps.Application','TradeOps.Domain','TradeOps.Infrastructure')){
    $path=Join-Path ([IO.Path]::GetDirectoryName($assembly)) ($name+'.dll')
    $hashes[$name]=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
}
$invoked=[DateTimeOffset]::UtcNow
$payload=$null;$idSecure=$null;$secretSecure=$null;$pinSecure=$null;$process=$null;$safe=$null;$toolExit=1;$started=$false
try{
    $vault=Join-Path $env:LOCALAPPDATA 'TradeOps/Credentials/Deribit.Mainnet.ReadOnly.dpapi.json'
    $pinPath=Join-Path $repo '.tradeops/b3-private-session/qualification-profile.dpapi.json'
    foreach($path in @($vault,$pinPath,$out,$assembly)){
        if((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'REPARSE_POINT_REFUSED'}
    }
    $saved=Get-Content -LiteralPath $vault -Raw|ConvertFrom-Json
    $envelope=Get-Content -LiteralPath $pinPath -Raw|ConvertFrom-Json
    if($saved.schema -ne 1 -or $saved.environment -cne 'DeribitMainnetReadOnly' -or $envelope.schema -ne 1 -or $envelope.encryption -cne 'WindowsDPAPI-CurrentUser'){throw 'SAVED_BINDING_FORMAT_REFUSED'}
    $idSecure=ConvertTo-SecureString $saved.client_id_dpapi
    $secretSecure=ConvertTo-SecureString $saved.client_secret_dpapi
    $pinSecure=ConvertTo-SecureString $envelope.payload
    $id=([Management.Automation.PSCredential]::new('local',$idSecure)).GetNetworkCredential().Password
    $secret=([Management.Automation.PSCredential]::new('local',$secretSecure)).GetNetworkCredential().Password
    $pin=([Management.Automation.PSCredential]::new('local',$pinSecure)).GetNetworkCredential().Password|ConvertFrom-Json
    $fingerprint=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($id))).ToLowerInvariant()
    if($pin.environment -cne 'Mainnet' -or $pin.expectedAccountId -le 0 -or $pin.accountType -cnotin @('main','subaccount') -or $pin.credentialIdSha256 -cne $fingerprint){throw 'NATIVE_PIN_REFUSED'}
    $payload=@{Purpose='PINNED_ACCOUNT_RECOVERY_ASSESSMENT_V1';Environment='Mainnet';ClientId=$id;ClientSecret=$secret;
        ExpectedAccountId=$pin.expectedAccountId;AccountType=$pin.accountType;FromUtc=$FromUtc.ToUniversalTime().ToString('o');ThroughUtc=$ThroughUtc.ToUniversalTime().ToString('o')}|ConvertTo-Json -Compress
    $process=[Diagnostics.Process]::new()
    $process.StartInfo=[Diagnostics.ProcessStartInfo]::new('wsl.exe')
    foreach($arg in @('-d','Ubuntu','-u','root','--','bash','/mnt/d/Projects/TradeOps-v2/scripts/testing/mainnet-recovery-reader.sh','--assess-account-recovery')){$process.StartInfo.ArgumentList.Add($arg)}
    $process.StartInfo.UseShellExecute=$false;$process.StartInfo.CreateNoWindow=$true
    $process.StartInfo.RedirectStandardInput=$true;$process.StartInfo.RedirectStandardOutput=$true;$process.StartInfo.RedirectStandardError=$true
    $process.StartInfo.Environment['DeribitTerminal__ExecutionEnabled']='false'
    $process.StartInfo.Environment['DeribitTerminal__MainnetExecutionEnabled']='false'
    if(!$process.Start()){throw 'ACCOUNT_READER_START_FAILED'}
    $started=$true
    $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
    $process.StandardInput.WriteLine($payload);$process.StandardInput.Close()
    # Drop plaintext references immediately after handing stdin to this one owned child.
    $payload=$null;$id=$null;$secret=$null;$pin=$null;$saved=$null
    if(!$process.WaitForExit(150000)){
        $process.Kill();$null=$process.WaitForExit(5000)
        throw 'ACCOUNT_READER_DEADLINE'
    }
    $toolExit=$process.ExitCode
    $null=$stderr.GetAwaiter().GetResult() # Never print or persist raw stderr.
    $rawText=$stdout.GetAwaiter().GetResult()
    if($rawText.Length -gt 16384){throw 'DIAGNOSTICS_BOUND_REFUSED'}
    $lines=@($rawText -split '\r?\n')
    $frames=@($lines|Where-Object {$_.StartsWith('{')})
    $evidence=@($lines|Where-Object {$_ -match '^RECOVERY_EVIDENCE=\.tradeops/mainnet-recovery/[A-Za-z0-9_-]+ EXIT=0$'})
    if($frames.Count -ne 1 -or $evidence.Count -ne 1){throw 'ENVIRONMENT_EVIDENCE_REFUSED'}
    $safe=ConvertTo-SafeAccountRecovery -rawText $frames[0] -toolExit $toolExit -invoked $invoked
    $safe['EnvironmentEvidence']=$evidence[0]
    # Rebuild a validated payload only; unknown child fields cannot enter evidence.
    foreach($name in $hashes.Keys){
        $path=Join-Path ([IO.Path]::GetDirectoryName($assembly)) ($name+'.dll')
        if((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne $hashes[$name]){throw 'ASSEMBLY_CHANGED'}
    }
    $currentHead=(& git -C $repo rev-parse HEAD).Trim()
    if($LASTEXITCODE -ne 0 -or $currentHead -cne $head){throw 'SOURCE_CHANGED'}
    & git -C $repo diff --quiet HEAD --
    if($LASTEXITCODE -ne 0){throw 'SOURCE_CHANGED'}
}catch{
    $toolExit=1
    $safe=[ordered]@{Status='RECOVERY_ACCOUNT_LOCAL_REFUSED';Stage='LocalWrapper';TradingReady=$false;DurableWrites=0;MutatingCalls=0}
}finally{
    $payload=$null;$id=$null;$secret=$null;$pin=$null;$saved=$null;$raw=$null;$rawText=$null
    foreach($secure in @($idSecure,$secretSecure,$pinSecure)){if($secure){$secure.Dispose()}}
    if($process){
        if($started -and !$process.HasExited){$process.Kill();$null=$process.WaitForExit(5000)}
        $process.Dispose()
    }
}
$safe['SourceCommit']=$head;$safe['AssemblyHashes']=$hashes;$safe['InvokedAtUtc']=$invoked.ToString('o')
[IO.File]::WriteAllText((Join-Path $out 'summary.json'),($safe|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
$safe|ConvertTo-Json -Depth 8 -Compress
if($toolExit -ne 0 -or $safe.Status -cne 'RECOVERY_ACCOUNT_EVIDENCE_BLOCKED'){throw 'ACCOUNT_RECOVERY_NOT_QUALIFIED; no automatic retry'}
