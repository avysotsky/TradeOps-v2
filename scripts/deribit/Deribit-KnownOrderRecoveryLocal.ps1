#requires -Version 7.5
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$head=(& git -C $repo rev-parse HEAD).Trim()
if($LASTEXITCODE -ne 0){throw 'SOURCE_HEAD_REQUIRED'}
& git -C $repo diff --quiet HEAD --
if($LASTEXITCODE -ne 0){throw 'COMMITTED_SOURCE_REQUIRED'}
$untracked=@(& git -C $repo ls-files --others --exclude-standard)
if($LASTEXITCODE -ne 0 -or @($untracked|Where-Object {$_ -cne 'src/TradeOps.Api/Properties/launchSettings.json'}).Count){throw 'UNTRACKED_SOURCE_REFUSED'}
$out=Join-Path $repo ('.tradeops/known-order-recovery/'+[DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmss')+'-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $out -Force|Out-Null
$assembly=Join-Path $repo 'tools/TradeOps.DeribitMainnetEthQualification/bin/Release/net10.0/TradeOps.DeribitMainnetEthQualification.dll'
$hashes=[ordered]@{}
foreach($name in @('TradeOps.DeribitMainnetEthQualification','TradeOps.Connectors.Deribit','TradeOps.Application','TradeOps.Domain')){
    $path=Join-Path ([IO.Path]::GetDirectoryName($assembly)) ($name+'.dll')
    $hashes[$name]=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
}
$payload=$null;$idSecure=$null;$secretSecure=$null;$pinSecure=$null
try{
    $vault=Join-Path $env:LOCALAPPDATA 'TradeOps/Credentials/Deribit.Mainnet.ReadOnly.dpapi.json'
    $pinPath=Join-Path $repo '.tradeops/b3-private-session/qualification-profile.dpapi.json'
    foreach($path in @($vault,$pinPath,$out)){
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
    $payload=@{ClientId=$id;ClientSecret=$secret;ExpectedAccountId=$pin.expectedAccountId;AccountType=$pin.accountType;
        Purpose='PINNED_KNOWN_ORDER_READ'}|ConvertTo-Json -Compress
    $process=[Diagnostics.Process]::new()
    $process.StartInfo=[Diagnostics.ProcessStartInfo]::new('wsl.exe')
    foreach($arg in @('-d','Ubuntu','-u','root','--','bash','/mnt/d/Projects/TradeOps-v2/scripts/testing/mainnet-recovery-reader.sh','--read-known-orders')){$process.StartInfo.ArgumentList.Add($arg)}
    $process.StartInfo.UseShellExecute=$false;$process.StartInfo.CreateNoWindow=$true
    $process.StartInfo.RedirectStandardInput=$true;$process.StartInfo.RedirectStandardOutput=$true;$process.StartInfo.RedirectStandardError=$true
    $process.StartInfo.Environment['DeribitTerminal__ExecutionEnabled']='false'
    $process.StartInfo.Environment['DeribitTerminal__MainnetExecutionEnabled']='false'
    if(!$process.Start()){throw 'COLLECTOR_START_FAILED'}
    $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
    $process.StandardInput.WriteLine($payload);$process.StandardInput.Close()
}finally{
    $payload=$null;$id=$null;$secret=$null;$pin=$null;$saved=$null
    foreach($secure in @($idSecure,$secretSecure,$pinSecure)){if($secure){$secure.Dispose()}}
}
if(!$process.WaitForExit(90000)){throw 'COLLECTION_DEADLINE; inspect local evidence'}
$null=$stderr.GetAwaiter().GetResult()
$lines=@($stdout.GetAwaiter().GetResult() -split '\r?\n')
$frames=@($lines|Where-Object {$_.StartsWith('{')})
if($frames.Count -ne 1){throw 'KNOWN_ORDER_RESULT_REFUSED'}
$result=$frames[0]|ConvertFrom-Json -DateKind String
$evidence=@($lines|Where-Object {$_ -match '^RECOVERY_EVIDENCE=\.tradeops/mainnet-recovery/[A-Za-z0-9_-]+ EXIT=0$'})
if($evidence.Count -ne 1){throw 'KNOWN_ORDER_ENVIRONMENT_FAILED'}
$result|Add-Member -NotePropertyName EnvironmentEvidence -NotePropertyValue $evidence[0]
$result|Add-Member -NotePropertyName SourceCommit -NotePropertyValue $head
$result|Add-Member -NotePropertyName AssemblyHashes -NotePropertyValue $hashes
[IO.File]::WriteAllText((Join-Path $out 'summary.json'),($result|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
$result|ConvertTo-Json -Depth 8 -Compress
if($process.ExitCode -ne 0 -or $result.Status -cnotin @('KNOWN_ORDERS_FOUND','KNOWN_ORDERS_UNRESOLVED')){throw 'RECOVERY_COLLECTION_INCOMPLETE; no automatic retry'}
