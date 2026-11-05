#requires -Version 7.5
# Exercise only the wrapper's actual output validator; never execute its vault/process entry.
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$tokens=$null;$parseErrors=$null
$path=Join-Path $PSScriptRoot '../deribit/Deribit-AccountSnapshotLocal.ps1'
$ast=[Management.Automation.Language.Parser]::ParseFile($path,[ref]$tokens,[ref]$parseErrors)
if($parseErrors.Count){throw 'WRAPPER_PARSE_FAILED'}
$definition=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'ConvertTo-SafeAccountSnapshot'},$false)
if(!$definition){throw 'VALIDATOR_REQUIRED'}
. ([scriptblock]::Create($definition.Extent.Text))
$script:passed=0
function Fixture {
    [ordered]@{Status='ACCOUNT_SNAPSHOT_SCOPED_PASS';Stage='Completed';MetadataValidated=$true;QueriesCompleted=$true;
        NativeBindingVerified=$true;FreshAtAssessment=$true;PositionCount=1;BalanceObserved=$true;
        AssessedAtUtc=[DateTimeOffset]::UtcNow.ToString('o');Problems=@();Currency='ETH';Kind='future';RequestedInstrumentCount=1;
        AccountWideCoverage=$false;VenueCoherent=$false;AccountReady=$false;DurableProjectionPublished=$false;
        TradingReady=$false;DatabaseUsed=$false;DurableWrites=0;MutatingCalls=0}
}
function Check([object]$value,[int]$code,[bool]$accept,[string]$name) {
    $json=if($value -is [string]){$value}else{$value|ConvertTo-Json -Depth 8 -Compress}
    $accepted=$false
    try{
        $safe=ConvertTo-SafeAccountSnapshot $json $code ([DateTimeOffset]::UtcNow.AddSeconds(-1))
        $accepted=$true
        if(($safe|ConvertTo-Json -Depth 8 -Compress).Contains('synthetic-private-leak')){throw 'PRIVATE_OUTPUT'}
    }catch{if($accepted){throw 'VALIDATOR_LEAKED'}}
    if($accepted -ne $accept){throw ('DIAGNOSTICS_CASE_FAILED: '+$name)}
    $script:passed++
}
Check (Fixture) 0 $true 'pass'
$v=Fixture;$v.Status='ACCOUNT_SNAPSHOT_INCOMPLETE';$v.QueriesCompleted=$false;$v.NativeBindingVerified=$false
$v.FreshAtAssessment=$false;$v.BalanceObserved=$false;$v.PositionCount=0;$v.Problems=@('UnmappedInstrument')
Check $v 1 $true 'incomplete'
$v=Fixture;$v.Status='ACCOUNT_SNAPSHOT_REFUSED';$v.Stage='Input';$v.MetadataValidated=$false
$v.QueriesCompleted=$false;$v.NativeBindingVerified=$false;$v.FreshAtAssessment=$false;$v.BalanceObserved=$false;$v.PositionCount=0
Check $v 1 $true 'refused'
$v=Fixture;$v.Status='ACCOUNT_SNAPSHOT_INCOMPLETE';$v.FreshAtAssessment=$false;$v.BalanceObserved=$false;$v.PositionCount=0;$v.Problems=@('StaleObservation')
Check $v 1 $true 'expired-at-assessment'
foreach($mode in @('extra','unknown-problem','nested-problem','count-string','bool-string','missing','status-array','ready','database','mutation',
    'future-time','old-time','exit','no-balance','no-problem','refused-completed','large','invalid-json')){
    $v=Fixture;$code=0
    switch($mode){
        'extra' {$v['NativeAccountId']='synthetic-private-leak'}
        'unknown-problem' {$v.Problems=@('synthetic-private-leak')}
        'nested-problem' {$v.Problems=@(@{private='synthetic-private-leak'})}
        'count-string' {$v.PositionCount='synthetic-private-leak'}
        'bool-string' {$v.MetadataValidated='true'}
        'missing' {$v.Remove('Problems')}
        'status-array' {$v.Status=@('ACCOUNT_SNAPSHOT_SCOPED_PASS','synthetic-private-leak')}
        'ready' {$v.AccountReady=$true}
        'database' {$v.DatabaseUsed=$true}
        'mutation' {$v.MutatingCalls=1}
        'future-time' {$v.AssessedAtUtc=[DateTimeOffset]::UtcNow.AddMinutes(1).ToString('o')}
        'old-time' {$v.AssessedAtUtc=[DateTimeOffset]::UtcNow.AddMinutes(-1).ToString('o')}
        'exit' {$code=1}
        'no-balance' {$v.BalanceObserved=$false}
        'no-problem' {$code=1;$v.Status='ACCOUNT_SNAPSHOT_INCOMPLETE';$v.FreshAtAssessment=$false;$v.BalanceObserved=$false;$v.PositionCount=0}
        'refused-completed' {$code=1;$v.Status='ACCOUNT_SNAPSHOT_REFUSED';$v.FreshAtAssessment=$false;$v.BalanceObserved=$false;$v.PositionCount=0}
        'large' {$v='x'*16385}
        'invalid-json' {$v='{synthetic-private-leak'}
    }
    Check $v $code $false $mode
}
[ordered]@{Status='PASS';Cases=$script:passed;VaultUsed=$false;VenueUsed=$false}|ConvertTo-Json -Compress
