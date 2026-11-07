#requires -Version 7.5
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
function ConvertFrom-SafeFixConfigurationCheck {
    param([string]$Text,[int]$ToolExit)
    $keys=@('ConnectionStrings:TradeOpsCaptureDb','ConnectionStrings:TradeOpsRecoveryDb','ConnectionStrings:TradeOpsExecutionDb',
        'ConnectionStrings:TradeOpsFixDb','DeribitOms:LockRoot','DeribitFix:SenderCompId','DeribitCapture:Tenant','DeribitCapture:Account','DeribitRecovery:InitialFromUtc')
    if($text.Length -gt 16384){throw 'CONFIGURATION_CHECK_OUTPUT_BOUND'}
    try{$result=$Text.Trim()|ConvertFrom-Json -Depth 8}catch{throw 'CONFIGURATION_CHECK_OUTPUT_JSON'}
    $fields=@('SchemaVersion','Status','Checks','VaultUsed','VenueUsed','TradingStarted','LiveAuthorityEstablished','NativeAccountVerified')
    if($result -isnot [PSCustomObject] -or @($result.PSObject.Properties).Count -ne $fields.Count -or
        @($result.PSObject.Properties.Name|Where-Object {$_ -cnotin $fields}).Count){throw 'CONFIGURATION_CHECK_OUTPUT_SCHEMA'}
    if(($result.SchemaVersion -isnot [int] -and $result.SchemaVersion -isnot [long]) -or $result.SchemaVersion -ne 1 -or
        $result.Status -cnotin @('FIX_CONFIGURATION_CHECK_PASS','FIX_CONFIGURATION_CHECK_BLOCKED')){throw 'CONFIGURATION_CHECK_OUTPUT_VERSION'}
    foreach($key in @('VaultUsed','VenueUsed','TradingStarted','LiveAuthorityEstablished','NativeAccountVerified')){
        if($result.$key -isnot [bool] -or $result.$key){throw 'CONFIGURATION_CHECK_OUTPUT_AUTHORITY'}
    }
    $allowed=@($keys|ForEach-Object {'Setting:'+$_})+@('ConfigurationShape','ConfigurationShapeOrInspection','ReadOnlyConnections','FourDistinctDatabaseIdentities',
        'CaptureRole','RecoveryRole','ExecutionRole','ProtocolRole','AmendmentSchemaV2','ProvisionedFixSession','CurrentEthAmendmentRiskPolicy','ExistingNodeObjectBinding')
    if($result.Checks -isnot [array] -or $result.Checks.Count -lt 9 -or $result.Checks.Count -gt 21){throw 'CONFIGURATION_CHECK_OUTPUT_CHECKS'}
    foreach($check in $result.Checks){
        if($check -isnot [PSCustomObject] -or @($check.PSObject.Properties).Count -notin @(2,3) -or
            @($check.PSObject.Properties.Name|Where-Object {$_ -cnotin @('Code','Passed','Reason')}).Count -or $check.Code -isnot [string] -or
            $check.Code -cnotin $allowed -or $check.Passed -isnot [bool]){throw 'CONFIGURATION_CHECK_OUTPUT_CHECKS'}
        if($check.PSObject.Properties.Name -ccontains 'Reason'){
            if($check.Passed -or $check.Reason -cnotin @('Timeout','PermissionDenied','MissingObject','InvalidConfiguration','Unavailable','Unproven')){throw 'CONFIGURATION_CHECK_OUTPUT_REASON'}
        }
    }
    if(@($result.Checks.Code|Select-Object -Unique).Count -ne $result.Checks.Count){throw 'CONFIGURATION_CHECK_OUTPUT_DUPLICATE'}
    $pass=$result.Status -ceq 'FIX_CONFIGURATION_CHECK_PASS'
    if($pass -and ($ToolExit -ne 0 -or $result.Checks.Count -ne 20 -or @($result.Checks|Where-Object {!$_.Passed}).Count -or
        @($allowed|Where-Object {$_ -cne 'ConfigurationShapeOrInspection' -and $_ -cnotin $result.Checks.Code}).Count)){
        throw 'CONFIGURATION_CHECK_OUTPUT_PASS'
    }
    if(!$pass -and ($ToolExit -eq 0 -or !@($result.Checks|Where-Object {!$_.Passed}).Count)){throw 'CONFIGURATION_CHECK_OUTPUT_FAILURE'}
    return $result
}
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$keys=@('ConnectionStrings:TradeOpsCaptureDb','ConnectionStrings:TradeOpsRecoveryDb','ConnectionStrings:TradeOpsExecutionDb',
    'ConnectionStrings:TradeOpsFixDb','DeribitOms:LockRoot','DeribitFix:SenderCompId','DeribitCapture:Tenant','DeribitCapture:Account','DeribitRecovery:InitialFromUtc')
$settings=@($keys|ForEach-Object {[ordered]@{Code='Setting:'+$_;Passed=![string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($_.Replace(':','__')))}})
if(@($settings|Where-Object {!$_.Passed}).Count){
    [ordered]@{SchemaVersion=1;Status='FIX_CONFIGURATION_CHECK_BLOCKED';Checks=$settings;VaultUsed=$false;VenueUsed=$false;
        TradingStarted=$false;LiveAuthorityEstablished=$false;NativeAccountVerified=$false}|ConvertTo-Json -Depth 6 -Compress
    exit 1
}
$previous=$env:DOTNET_CLI_USE_MSBUILD_SERVER
try{
    $env:DOTNET_CLI_USE_MSBUILD_SERVER='0'
    $directory=Join-Path $repo '.tradeops/fix-qualification';$null=New-Item -ItemType Directory -Force -Path $directory
    & D:\DotNet\dotnet.exe build (Join-Path $repo 'src/TradeOps.Worker/TradeOps.Worker.csproj') -c Release --no-restore -m:1 -nr:false -p:BuildInParallel=false -p:UseSharedCompilation=false *> (Join-Path $directory 'configuration-check-build.log')
    if($LASTEXITCODE -ne 0){throw 'CONFIGURATION_CHECK_BUILD_REFUSED'}
}finally{$env:DOTNET_CLI_USE_MSBUILD_SERVER=$previous}
$process=[Diagnostics.Process]::new();$started=$false
try{
    $process.StartInfo=[Diagnostics.ProcessStartInfo]::new('D:\DotNet\dotnet.exe')
    $process.StartInfo.ArgumentList.Add((Join-Path $repo 'src/TradeOps.Worker/bin/Release/net10.0/TradeOps.Worker.dll'))
    $process.StartInfo.WorkingDirectory=Join-Path $repo 'src/TradeOps.Worker'
    $process.StartInfo.UseShellExecute=$false;$process.StartInfo.CreateNoWindow=$true
    $process.StartInfo.RedirectStandardOutput=$true;$process.StartInfo.RedirectStandardError=$true
    foreach($key in @($process.StartInfo.Environment.Keys)){
        if($key -match '^DeribitCapture__(ClientId|ClientSecret|ExpectedAccountId|ExpectedAccountType)$'){$null=$process.StartInfo.Environment.Remove($key)}
    }
    $process.StartInfo.Environment['Worker__Mode']='DeribitFixConfigurationCheck'
    foreach($key in @('DeribitOms__Enabled','DeribitFix__Enabled','DeribitFixQualification__OwnerLaunch','DeribitTerminal__ExecutionEnabled','DeribitTerminal__MainnetExecutionEnabled')){
        $process.StartInfo.Environment[$key]='false'
    }
    if(!$process.Start()){throw 'CONFIGURATION_CHECK_START_REFUSED'};$started=$true
    $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
    if(!$process.WaitForExit(60000)){throw 'CONFIGURATION_CHECK_TIMEOUT'}
    $null=$stderr.GetAwaiter().GetResult();$text=$stdout.GetAwaiter().GetResult()
    $result=ConvertFrom-SafeFixConfigurationCheck -Text $text -ToolExit $process.ExitCode
    $result|ConvertTo-Json -Depth 6 -Compress
    exit $process.ExitCode
}finally{if(!$started -or $process.HasExited){$process.Dispose()}}
