#requires -Version 7.5
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$errors=$null;$tokens=$null
$ast=[System.Management.Automation.Language.Parser]::ParseFile((Join-Path $repo 'scripts/deribit/Deribit-FixConfigurationCheck.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'CHECKER_PARSE_FAILED'}
$function=$ast.Find({param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'ConvertFrom-SafeFixConfigurationCheck'},$true)
if(!$function){throw 'CHECKER_FUNCTION_MISSING'}
. ([scriptblock]::Create($function.Extent.Text))
$codes=@('ConnectionStrings:TradeOpsCaptureDb','ConnectionStrings:TradeOpsRecoveryDb','ConnectionStrings:TradeOpsExecutionDb',
    'ConnectionStrings:TradeOpsFixDb','DeribitOms:LockRoot','DeribitFix:SenderCompId','DeribitCapture:Tenant','DeribitCapture:Account','DeribitRecovery:InitialFromUtc'|
    ForEach-Object {'Setting:'+$_})+@('ConfigurationShape','ReadOnlyConnections','FourDistinctDatabaseIdentities','CaptureRole','RecoveryRole','ExecutionRole',
        'ProtocolRole','AmendmentSchemaV2','ProvisionedFixSession','CurrentEthAmendmentRiskPolicy','ExistingNodeObjectBinding')
function Fixture {
    [ordered]@{SchemaVersion=1;Status='FIX_CONFIGURATION_CHECK_PASS';Checks=@($codes|ForEach-Object {[ordered]@{Code=$_;Passed=$true}});
        VaultUsed=$false;VenueUsed=$false;TradingStarted=$false;LiveAuthorityEstablished=$false;NativeAccountVerified=$false}|ConvertTo-Json -Depth 8|ConvertFrom-Json -Depth 8
}
$cases=0
function Check([object]$value,[int]$toolExit,[bool]$accept){
    $ok=$true
    try{$null=ConvertFrom-SafeFixConfigurationCheck -Text ($value|ConvertTo-Json -Depth 8 -Compress) -ToolExit $toolExit}catch{$ok=$false}
    if($ok -ne $accept){throw 'CHECKER_CASE_FAILED'}
    $script:cases++
}
Check (Fixture) 0 $true
$value=Fixture;$value.Status='FIX_CONFIGURATION_CHECK_BLOCKED';$value.Checks[0].Passed=$false;Check $value 1 $true
$value.Checks[0]|Add-Member Reason Timeout;Check $value 1 $true
foreach($key in @('VaultUsed','VenueUsed','TradingStarted','LiveAuthorityEstablished','NativeAccountVerified')){
    $value=Fixture;$value.$key=$true;Check $value 0 $false
    $value.$key='false';Check $value 0 $false
}
$value=Fixture;$value.SchemaVersion='1';Check $value 0 $false
$value=Fixture;$value.SchemaVersion=2;Check $value 0 $false
$value=Fixture;$value|Add-Member NativeAccountId 123;Check $value 0 $false
$value=Fixture;$value.Status='TRADING_READY';Check $value 0 $false
$value=Fixture;$value.Checks[0].Passed='true';Check $value 0 $false
$value=Fixture;$value.Checks[0].Code='synthetic-secret';Check $value 0 $false
$value=Fixture;$value.Checks[0]|Add-Member NativeOrderId 'private';Check $value 0 $false
$value=Fixture;$value.Checks[0]|Add-Member Reason Timeout;Check $value 0 $false
$value=Fixture;$value.Checks[0].Code=$value.Checks[1].Code;Check $value 0 $false
$value=Fixture;$value.Checks=$value.Checks[1..19];Check $value 0 $false
$value=Fixture;$value.Checks[0].Code='ConfigurationShapeOrInspection';Check $value 0 $false
Check (Fixture) 1 $false
$value=Fixture;$value.Status='FIX_CONFIGURATION_CHECK_BLOCKED';Check $value 1 $false
$value.Checks[0].Passed=$false;Check $value 0 $false
$value.Checks[0]|Add-Member Reason 'private-value';Check $value 1 $false
foreach($text in @('{not-json','{}','[]',(' ' * 16385))){
    $accepted=$true;try{$null=ConvertFrom-SafeFixConfigurationCheck -Text $text -ToolExit 0}catch{$accepted=$false}
    if($accepted){throw 'CHECKER_INVALID_TEXT_ACCEPTED'};$cases++
}
[ordered]@{Status='PASS';Cases=$cases;VaultUsed=$false;VenueUsed=$false;TradingStarted=$false}|ConvertTo-Json -Compress
