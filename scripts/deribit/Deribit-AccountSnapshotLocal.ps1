#requires -Version 7.5
[CmdletBinding()]
param([ValidateSet('EthPerpetual','EthInverseFutures')][string]$Manifest='EthPerpetual')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
function ConvertTo-SafeAccountSnapshot {
    param([string]$rawText,[int]$toolExit,[DateTimeOffset]$invoked,
        [ValidateSet('EthPerpetualV1','EthActiveInverseFuturesV1')][string]$expectedManifest='EthPerpetualV1')
    if($rawText.Length -gt 16384){throw 'DIAGNOSTICS_BOUND_REFUSED'}
    $raw=$rawText|ConvertFrom-Json -Depth 8 -DateKind String
    $keys=@('Status','Stage','MetadataValidated','QueriesCompleted','NativeBindingVerified','FreshAtAssessment','PositionCount',
        'BalanceObserved','AssessedAtUtc','Problems','Currency','Kind','RequestedInstrumentCount','AccountWideCoverage',
        'VenueCoherent','AccountReady','DurableProjectionPublished','TradingReady','DatabaseUsed','DurableWrites','MutatingCalls',
        'SchemaVersion','ManifestPolicy')
    if($raw -isnot [PSCustomObject] -or @($raw.PSObject.Properties).Count -ne $keys.Count -or
        @($raw.PSObject.Properties.Name|Where-Object {$_ -cnotin $keys}).Count){throw 'DIAGNOSTICS_SCHEMA_REFUSED'}
    $safe=[ordered]@{}
    foreach($key in $keys){$safe[$key]=$raw.$key}
    foreach($key in @('Status','Stage','Currency','Kind','ManifestPolicy')){
        if($safe[$key] -isnot [string]){throw 'DIAGNOSTICS_TYPE_REFUSED'}
    }
    foreach($key in @('PositionCount','RequestedInstrumentCount','DurableWrites','MutatingCalls','SchemaVersion')){
        if($safe[$key] -isnot [long] -and $safe[$key] -isnot [int]){throw 'DIAGNOSTICS_TYPE_REFUSED'}
    }
    foreach($key in @('MetadataValidated','QueriesCompleted','NativeBindingVerified','FreshAtAssessment','BalanceObserved',
        'AccountWideCoverage','VenueCoherent','AccountReady','DurableProjectionPublished','TradingReady','DatabaseUsed')){
        if($safe[$key] -isnot [bool]){throw 'DIAGNOSTICS_TYPE_REFUSED'}
    }
    if($safe.Status -cnotin @('ACCOUNT_SNAPSHOT_SCOPED_PASS','ACCOUNT_SNAPSHOT_INCOMPLETE','ACCOUNT_SNAPSHOT_REFUSED') -or
        $safe.Stage -cnotin @('Input','Metadata','AccountQueries','Completed') -or $safe.Currency -cne 'ETH' -or $safe.Kind -cne 'future' -or
        $safe.SchemaVersion -ne 2 -or $safe.ManifestPolicy -cne $expectedManifest -or
        $safe.RequestedInstrumentCount -lt 0 -or $safe.RequestedInstrumentCount -gt 256 -or
        $safe.PositionCount -lt 0 -or $safe.PositionCount -gt 256 -or
        $safe.AccountWideCoverage -or $safe.VenueCoherent -or $safe.AccountReady -or $safe.DurableProjectionPublished -or
        $safe.TradingReady -or $safe.DatabaseUsed -or $safe.DurableWrites -ne 0 -or $safe.MutatingCalls -ne 0){throw 'DIAGNOSTICS_AUTHORITY_REFUSED'}
    if($expectedManifest -ceq 'EthPerpetualV1'){
        if($safe.RequestedInstrumentCount -ne 1){throw 'DIAGNOSTICS_MANIFEST_REFUSED'}
    }elseif(($safe.MetadataValidated -and $safe.RequestedInstrumentCount -lt 1) -or
        (!$safe.MetadataValidated -and $safe.RequestedInstrumentCount -ne 0)){throw 'DIAGNOSTICS_MANIFEST_REFUSED'}
    if($safe.PositionCount -gt $safe.RequestedInstrumentCount){throw 'DIAGNOSTICS_MANIFEST_REFUSED'}
    if($safe.AssessedAtUtc -isnot [string] -or $safe.AssessedAtUtc.Length -gt 64){throw 'DIAGNOSTICS_TIME_REFUSED'}
    $assessed=[DateTimeOffset]::Parse($safe.AssessedAtUtc,[Globalization.CultureInfo]::InvariantCulture)
    if($assessed.Offset -ne [TimeSpan]::Zero -or $assessed -lt $invoked.AddSeconds(-1) -or $assessed -gt [DateTimeOffset]::UtcNow.AddSeconds(5)){throw 'DIAGNOSTICS_TIME_REFUSED'}
    $knownProblems=@('Authentication','AccountMismatch','Protocol','Transport','Capacity','Deadline','UnmappedInstrument','StaleObservation')
    if($safe.Problems -isnot [array] -or @($safe.Problems).Count -gt $knownProblems.Count -or
        @($safe.Problems|Where-Object {$_ -isnot [string] -or $_ -cnotin $knownProblems}).Count){throw 'DIAGNOSTICS_PROBLEMS_REFUSED'}
    $pass=$safe.Status -ceq 'ACCOUNT_SNAPSHOT_SCOPED_PASS'
    if($pass){
        if($toolExit -ne 0 -or $safe.Stage -cne 'Completed' -or !$safe.MetadataValidated -or !$safe.QueriesCompleted -or
            !$safe.NativeBindingVerified -or !$safe.FreshAtAssessment -or !$safe.BalanceObserved -or $safe.Problems.Count){throw 'DIAGNOSTICS_PASS_REFUSED'}
    }else{
        if($toolExit -eq 0 -or $safe.FreshAtAssessment -or $safe.BalanceObserved -or $safe.PositionCount -ne 0){throw 'DIAGNOSTICS_FAILURE_REFUSED'}
        if($safe.Status -ceq 'ACCOUNT_SNAPSHOT_INCOMPLETE'){
            if($safe.Stage -cne 'Completed' -or !$safe.MetadataValidated -or !$safe.Problems.Count -or
                $safe.NativeBindingVerified -ne $safe.QueriesCompleted){throw 'DIAGNOSTICS_FAILURE_REFUSED'}
        }elseif($safe.Stage -ceq 'Completed' -or $safe.QueriesCompleted -or $safe.NativeBindingVerified -or $safe.Problems.Count -or
            $safe.MetadataValidated -ne ($safe.Stage -ceq 'AccountQueries')){throw 'DIAGNOSTICS_FAILURE_REFUSED'}
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
$out=Join-Path $repo ('.tradeops/account-snapshot/'+[DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmss')+'-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $out -Force|Out-Null
$assembly=Join-Path $repo 'tools/TradeOps.DeribitMainnetEthQualification/bin/Release/net10.0/TradeOps.DeribitMainnetEthQualification.dll'
$hashes=[ordered]@{}
foreach($name in @('TradeOps.DeribitMainnetEthQualification','TradeOps.Connectors.Deribit','TradeOps.Application','TradeOps.Domain')){
    $path=Join-Path ([IO.Path]::GetDirectoryName($assembly)) ($name+'.dll')
    $hashes[$name]=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
}
$invoked=[DateTimeOffset]::UtcNow
$group=$Manifest -ceq 'EthInverseFutures'
$policy=if($group){'EthActiveInverseFuturesV1'}else{'EthPerpetualV1'}
$purpose=if($group){'READ_ONLY_ETH_INVERSE_FUTURES_V1'}else{'READ_ONLY_ETH_ACCOUNT_SNAPSHOT_V1'}
$mode=if($group){'--read-eth-future-snapshot'}else{'--read-account-snapshot'}
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
    $payload=@{Purpose=$purpose;Environment='Mainnet';ClientId=$id;ClientSecret=$secret;
        ExpectedAccountId=$pin.expectedAccountId;AccountType=$pin.accountType}|ConvertTo-Json -Compress
    $process=[Diagnostics.Process]::new()
    $process.StartInfo=[Diagnostics.ProcessStartInfo]::new('D:\DotNet\dotnet.exe')
    $process.StartInfo.ArgumentList.Add($assembly);$process.StartInfo.ArgumentList.Add($mode)
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
    if(!$process.WaitForExit(90000)){
        $process.Kill();$null=$process.WaitForExit(5000)
        throw 'ACCOUNT_READER_DEADLINE'
    }
    $toolExit=$process.ExitCode
    $null=$stderr.GetAwaiter().GetResult() # Never print or persist raw stderr.
    $rawText=$stdout.GetAwaiter().GetResult()
    if($rawText.Length -gt 16384){throw 'DIAGNOSTICS_BOUND_REFUSED'}
    $safe=ConvertTo-SafeAccountSnapshot -rawText $rawText -toolExit $toolExit -invoked $invoked -expectedManifest $policy
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
    $safe=[ordered]@{Status='ACCOUNT_SNAPSHOT_LOCAL_REFUSED';Stage='LocalWrapper';TradingReady=$false;
        DatabaseUsed=$false;DurableWrites=0;MutatingCalls=0}
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
if($toolExit -ne 0 -or $safe.Status -cne 'ACCOUNT_SNAPSHOT_SCOPED_PASS'){throw 'ACCOUNT_SNAPSHOT_NOT_QUALIFIED; no automatic retry'}
