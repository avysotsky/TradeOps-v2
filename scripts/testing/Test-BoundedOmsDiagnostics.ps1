#requires -Version 7.5
# Extract only the validator; never invoke the owner launcher, vault, WSL or venue.
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot '../deribit/Deribit-BoundedOmsLocal.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'BOUNDED_WRAPPER_PARSE_FAILED'}
$definition=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'ConvertTo-SafeBoundedOms'},$false)
if(!$definition){throw 'BOUNDED_VALIDATOR_MISSING'}
. ([scriptblock]::Create($definition.Extent.Text))
$script:cases=0
function Fixture {
    [ordered]@{Status='OMS_SCENARIO_PASS';Stage='Completed';DatabaseUsed=$true;PersistentStore=$true;StagePlacementsUsed=1;
        PlacementAttempts=1;CancellationAttempts=1;ReceiptsRecorded=2;OpenEventCommitted=$true;CancelEventCommitted=$true;UnexpectedFill=$false;
        OwnOrderMayRemain=$false;BaselinePositionPreserved=$true;CurrentCommandsValidated=$true;AssessmentCompleted=$true;RemainingCommandIssues=2;
        RecoveryBlockers=@('CoherentCutUnavailable','BufferedReplayUnavailable','ExecutionLedgerCoverageUnproven','CommandRecoveryUnresolved');
        SchemaVersion=1;Instrument='ETH-PERPETUAL';MaximumNativeUsd=1;MaximumStagePlacements=3;MinimumDistanceFraction=.10;
        PostOnly=$true;AutomaticFlatten=$false;AccountReady=$false;TradingReady=$false;DurableProjectionPublished=$false}
}
function Check($value,[int]$exitCode,[bool]$expect,[string]$name){
    $text=if($value -is [string]){$value}else{$value|ConvertTo-Json -Depth 8 -Compress}
    $accepted=$false
    try{$safe=ConvertTo-SafeBoundedOms $text $exitCode;$accepted=$true;if(($safe|ConvertTo-Json -Depth 8).Contains('synthetic-private-leak')){throw 'PRIVATE_LEAK'}}catch{if($accepted){throw}}
    if($accepted -ne $expect){throw ('BOUNDED_DIAGNOSTICS_FAILED: '+$name)}
    $script:cases++
}
Check (Fixture) 0 $true 'pass-is-not-ready'
$v=Fixture;$v.Status='OMS_SCENARIO_SKIPPED';$v.Stage='Metadata';$v.StagePlacementsUsed=$null;$v.PlacementAttempts=0;$v.CancellationAttempts=0;$v.ReceiptsRecorded=0
$v.OpenEventCommitted=$false;$v.CancelEventCommitted=$false;$v.BaselinePositionPreserved=$false;$v.CurrentCommandsValidated=$false;$v.AssessmentCompleted=$false;$v.RemainingCommandIssues=0;$v.RecoveryBlockers=@()
Check $v 0 $true 'skip-without-reserving'
$v.Status='OMS_SCENARIO_REFUSED';$v.Stage='Store';$v.PersistentStore=$false
Check $v 1 $true 'missing-store-with-no-trade'
$v=Fixture;$v.Status='OMS_SCENARIO_UNRESOLVED';$v.Stage='Placement';$v.CancellationAttempts=0;$v.ReceiptsRecorded=0;$v.OpenEventCommitted=$false;$v.CancelEventCommitted=$false
$v.BaselinePositionPreserved=$false;$v.CurrentCommandsValidated=$false;$v.AssessmentCompleted=$false;$v.RemainingCommandIssues=0;$v.RecoveryBlockers=@();$v.OwnOrderMayRemain=$true
Check $v 1 $true 'lost-placement-no-retry'
$v.Status='OMS_SCENARIO_STOPPED_FILL';$v.UnexpectedFill=$true;$v.ReceiptsRecorded=1
Check $v 1 $true 'fill-no-cancel-no-flatten'
foreach($mode in @('extra','missing','ready','flatten','repeat','budget','no-budget','size','distance','post-only','no-receipt','no-capture',
    'position-change','no-assessment','no-binding','no-ledger-gate','native-leak','bool-string','count-string','money-string','status-object','schema','bad-exit','invalid-json','duplicate-blocker')){
    $v=Fixture;$exitCode=0
    switch($mode){
        'extra' {$v['NativeAccount']='synthetic-private-leak'}
        'missing' {$v.Remove('ReceiptsRecorded')}
        'ready' {$v.AccountReady=$true}
        'flatten' {$v.AutomaticFlatten=$true}
        'repeat' {$v.PlacementAttempts=2}
        'budget' {$v.StagePlacementsUsed=4}
        'no-budget' {$v.StagePlacementsUsed=$null}
        'size' {$v.MaximumNativeUsd=2}
        'distance' {$v.MinimumDistanceFraction=.005}
        'post-only' {$v.PostOnly=$false}
        'no-receipt' {$v.ReceiptsRecorded=1}
        'no-capture' {$v.CancelEventCommitted=$false}
        'position-change' {$v.BaselinePositionPreserved=$false}
        'no-assessment' {$v.AssessmentCompleted=$false}
        'no-binding' {$v.CurrentCommandsValidated=$false}
        'no-ledger-gate' {$v.RecoveryBlockers=@('CoherentCutUnavailable','BufferedReplayUnavailable')}
        'native-leak' {$v.RecoveryBlockers+=@('synthetic-private-leak')}
        'bool-string' {$v.TradingReady='false'}
        'count-string' {$v.PlacementAttempts='1'}
        'money-string' {$v.MaximumNativeUsd='1'}
        'status-object' {$v.Status=@{value='OMS_SCENARIO_PASS'}}
        'schema' {$v.SchemaVersion=2}
        'bad-exit' {$exitCode=1}
        'invalid-json' {$v='{not JSON'}
        'duplicate-blocker' {$v.RecoveryBlockers+=@('CoherentCutUnavailable')}
    }
    Check $v $exitCode $false $mode
}
[ordered]@{Status='PASS';Cases=$script:cases;VaultUsed=$false;VenueUsed=$false;TradingStarted=$false}|ConvertTo-Json -Compress
