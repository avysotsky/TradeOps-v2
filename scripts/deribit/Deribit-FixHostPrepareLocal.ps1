#requires -Version 7.5
[CmdletBinding()]
param([ValidateSet('Status','ReadAccount','PrepareNode')][string]$Action='Status')
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if($Action -eq 'Status'){
    [ordered]@{Status='HOST_PREPARATION_ONLY';Actions=@('ReadAccount','PrepareNode');DatabaseUsed=$false;TradingStarted=$false}|ConvertTo-Json -Compress
    return
}
$head=(& git -C $repo rev-parse HEAD).Trim();if($LASTEXITCODE -ne 0){throw 'SOURCE_REQUIRED'}
function Assert-Source {
    & git -C $repo diff --quiet HEAD --;if($LASTEXITCODE -ne 0 -or (& git -C $repo rev-parse HEAD).Trim() -cne $head){throw 'COMMITTED_SOURCE_REQUIRED'}
    $files=@(& git -C $repo ls-files --others --exclude-standard|Where-Object {$_ -cne 'src/TradeOps.Api/Properties/launchSettings.json'})
    if($LASTEXITCODE -ne 0 -or $files.Count){throw 'UNTRACKED_SOURCE_REFUSED'}
}
Assert-Source
$folder=Join-Path $repo '.tradeops/fix-host';$null=New-Item -ItemType Directory -Force -Path $folder
$oldServer=$env:DOTNET_CLI_USE_MSBUILD_SERVER
try{
    $env:DOTNET_CLI_USE_MSBUILD_SERVER='0'
    & D:\DotNet\dotnet.exe build (Join-Path $repo 'tools/TradeOps.FixHostPreparation/TradeOps.FixHostPreparation.csproj') -c Release --no-restore -m:1 -nr:false -p:BuildInParallel=false -p:UseSharedCompilation=false *> (Join-Path $folder 'prepare-tool-build.log')
    if($LASTEXITCODE -ne 0){throw 'PREPARATION_BUILD_REFUSED'}
}finally{$env:DOTNET_CLI_USE_MSBUILD_SERVER=$oldServer}
$assembly=Join-Path $repo 'tools/TradeOps.FixHostPreparation/bin/Release/net10.0/TradeOps.FixHostPreparation.dll'
$hashes=@{};foreach($file in Get-ChildItem -LiteralPath (Split-Path $assembly) -File -Filter '*.dll'){$hashes[$file.FullName]=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash}
$idSecure=$null;$secretSecure=$null;$pinSecure=$null;$process=$null;$started=$false
try{
    $vault=Join-Path $env:LOCALAPPDATA 'TradeOps/Credentials/Deribit.Mainnet.ReadOnly.dpapi.json'
    $pinPath=Join-Path $repo '.tradeops/b3-private-session/qualification-profile.dpapi.json'
    $inventoryPath=Join-Path $repo '.tradeops/fix-provisioning/retained-store-inventory.private.json'
    foreach($path in @($vault,$pinPath,$inventoryPath,$folder,$assembly)){
        for($item=Get-Item -LiteralPath $path -Force;$item;$item=$item.Parent){if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'LINK_REFUSED'};if($item -is [IO.FileInfo]){$item=$item.Directory; if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'LINK_REFUSED'}}}
    }
    $saved=Get-Content -LiteralPath $vault -Raw|ConvertFrom-Json;$envelope=Get-Content -LiteralPath $pinPath -Raw|ConvertFrom-Json
    if($saved.schema -ne 1 -or $saved.environment -cne 'DeribitMainnetReadOnly' -or $envelope.schema -ne 1 -or $envelope.encryption -cne 'WindowsDPAPI-CurrentUser'){throw 'BINDING_FORMAT_REFUSED'}
    $idSecure=ConvertTo-SecureString $saved.client_id_dpapi;$pinSecure=ConvertTo-SecureString $envelope.payload
    $id=([Management.Automation.PSCredential]::new('local',$idSecure)).GetNetworkCredential().Password
    $pin=([Management.Automation.PSCredential]::new('local',$pinSecure)).GetNetworkCredential().Password|ConvertFrom-Json
    $fingerprint=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($id))).ToLowerInvariant()
    if($pin.environment -cne 'Mainnet' -or $pin.expectedAccountId -le 0 -or $pin.accountType -cnotin @('main','subaccount') -or $pin.credentialIdSha256 -cne $fingerprint){throw 'NATIVE_PIN_REFUSED'}
    $inventory=Get-Content -LiteralPath $inventoryPath -Raw|ConvertFrom-Json
    if(!$inventory.readonly -or @($inventory.scopes).Count -ne 1){throw 'RETAINED_SCOPE_REFUSED'}
    $scope=$inventory.scopes[0];$label='observed-native:'+[string]$pin.expectedAccountId
    if($scope.environment -ne 4 -or $scope.venue_id -cne 'DERIBIT' -or $scope.account_id -cne $label -or $scope.subaccount_id -cne $(if($pin.accountType -ceq 'subaccount'){$label}else{''})){throw 'RETAINED_SCOPE_REFUSED'}
    $private=Join-Path $folder ('account-'+[Guid]::NewGuid().ToString('N')+'.private.json')
    $target=if($Action -eq 'ReadAccount'){$private}else{Join-Path $repo '.tradeops/oms-execution'}
    $purpose=if($Action -eq 'ReadAccount'){'READ_ONLY_FIX_ACCOUNT_INVENTORY_V1'}else{'PREPARE_FIX_LOCAL_NODE_V1'}
    $mode=if($Action -eq 'ReadAccount'){'--read-account'}else{'--prepare-node'}
    $secret='';if($Action -eq 'ReadAccount'){$secretSecure=ConvertTo-SecureString $saved.client_secret_dpapi;$secret=([Management.Automation.PSCredential]::new('local',$secretSecure)).GetNetworkCredential().Password}
    $payload=@{Purpose=$purpose;Environment='Mainnet';Tenant=$scope.tenant_id;ExpectedAccountId=$pin.expectedAccountId;AccountType=$pin.accountType;
        ClientId=$(if($Action -eq 'ReadAccount'){$id}else{''});ClientSecret=$secret}|ConvertTo-Json -Compress
    Assert-Source
    $process=[Diagnostics.Process]::new();$process.StartInfo=[Diagnostics.ProcessStartInfo]::new('D:\DotNet\dotnet.exe')
    foreach($arg in @($assembly,$mode,$target)){$process.StartInfo.ArgumentList.Add($arg)}
    $process.StartInfo.WorkingDirectory=$repo;$process.StartInfo.UseShellExecute=$false;$process.StartInfo.CreateNoWindow=$true
    $process.StartInfo.RedirectStandardInput=$true;$process.StartInfo.RedirectStandardOutput=$true;$process.StartInfo.RedirectStandardError=$true
    foreach($key in @('DeribitTerminal__ExecutionEnabled','DeribitTerminal__MainnetExecutionEnabled')){$process.StartInfo.Environment[$key]='false'}
    if(!$process.Start()){throw 'PREPARATION_START_REFUSED'};$started=$true
    $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync();$process.StandardInput.WriteLine($payload);$process.StandardInput.Close()
    $payload=$null;$id=$null;$secret=$null;$pin=$null;$saved=$null
    if(!$process.WaitForExit(75000)){throw 'PREPARATION_TIMEOUT'}
    $null=$stderr.GetAwaiter().GetResult();$raw=$stdout.GetAwaiter().GetResult();if($raw.Length -gt 16384 -or $process.ExitCode -ne 0){throw 'PREPARATION_REFUSED'}
    $result=$raw|ConvertFrom-Json -Depth 8
    if($Action -eq 'ReadAccount'){
        $keys=@('Status','NativeBindingVerified','PositionCount','OpenOrderCount','CurrencyCount','CrossCollateralObserved','PortfolioMarginObserved','EstimatedEquityUsd','UsdEstimateComplete','ObservedUtc','DatabaseUsed','TradingStarted','AccountReady')
        if($result.Status -cne 'PINNED_ACCOUNT_INVENTORY_PASS' -or $result.NativeBindingVerified -cne $true -or $result.DatabaseUsed -cne $false -or $result.TradingStarted -cne $false -or $result.AccountReady -cne $false){throw 'PREPARATION_OUTPUT_REFUSED'}
    }else{
        $keys=@('SchemaVersion','NodeSha256','LockRoot','RootSha256','LockSha256')
        if($result.SchemaVersion -ne 2 -or $result.LockRoot -cne ([IO.Path]::GetFullPath($target).ToUpperInvariant())){throw 'NODE_OUTPUT_REFUSED'}
        foreach($key in @('NodeSha256','RootSha256','LockSha256')){if($result.$key -cnotmatch '^[A-F0-9]{64}$'){throw 'NODE_OUTPUT_REFUSED'}}
    }
    if(@($result.PSObject.Properties).Count -ne $keys.Count -or @($result.PSObject.Properties.Name|Where-Object {$_ -cnotin $keys}).Count){throw 'PREPARATION_OUTPUT_SCHEMA_REFUSED'}
    Assert-Source;foreach($path in $hashes.Keys){if((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne $hashes[$path]){throw 'PREPARATION_ASSEMBLY_CHANGED'}}
    $summary=@{SourceCommit=$head;Action=$Action;Diagnostics=$result;AssemblyHashes=$hashes;TradingStarted=$false;DatabaseUsed=$false}
    if($Action -eq 'ReadAccount'){$summary['PrivateArtifact']=$private;$summary['PrivateArtifactSha256']=(Get-FileHash -LiteralPath $private -Algorithm SHA256).Hash}
    [IO.File]::WriteAllText((Join-Path $folder ('latest-'+$Action.ToLowerInvariant()+'.json')),($summary|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
    if($Action -eq 'ReadAccount'){$result|ConvertTo-Json -Compress}else{[ordered]@{Status='LOCAL_NODE_PREPARED';DatabaseUsed=$false;TradingStarted=$false}|ConvertTo-Json -Compress}
}finally{
    $payload=$null;$id=$null;$secret=$null;$pin=$null;$saved=$null;$raw=$null
    foreach($secure in @($idSecure,$secretSecure,$pinSecure)){if($secure){$secure.Dispose()}}
    if($process){if($started -and !$process.HasExited){$process.Kill();$null=$process.WaitForExit(5000)};$process.Dispose()}
}
