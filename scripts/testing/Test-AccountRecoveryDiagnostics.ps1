#requires -Version 7.5
# Load only the real validator's AST definition, never the credential/process wrapper.
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot '../deribit/Deribit-AccountRecoveryLocal.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'WRAPPER_PARSE_FAILED'}
$definition=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'ConvertTo-SafeAccountRecovery'},$false)
if(!$definition){throw 'VALIDATOR_REQUIRED'}
. ([scriptblock]::Create($definition.Extent.Text))
$script:passed=0
function Fixture {
    [ordered]@{Status='RECOVERY_ACCOUNT_EVIDENCE_BLOCKED';Stage='Completed';MetadataValidated=$true;DatabaseUsed=$true;SelectOnly=$true;
        AssessmentCompleted=$true;RequestedInstruments=13;ScopedAccountObservationsValidated=$true;AcceptedPositionRows=2;BalanceObserved=$true;
        RetainedFacts=2;EnumeratedFacts=0;KnownOrders=1;AddressedOrders=0;RetentionExpiredOrders=1;FromUtc=[DateTimeOffset]::UtcNow.AddDays(-1).ToString('o');
        ThroughUtc=[DateTimeOffset]::UtcNow.AddSeconds(-5).ToString('o');AssessedAtUtc=[DateTimeOffset]::UtcNow.ToString('o');AccountProblems=@();
        Blockers=@('CoherentCutUnavailable','BufferedReplayUnavailable','ExecutionLedgerCoverageUnproven','AccountSnapshotCoherenceUnproven','AccountWideCoverageUnproven');
        SchemaVersion=2;RetainedTerminalOrdersValidated=1;OrderInstrument='ETH-PERPETUAL';ManifestPolicy='EthActiveInverseFuturesV1';Currency='ETH';Kind='future';
        AccountWideCoverage=$false;VenueCoherent=$false;AccountReady=$false;DurableProjectionPublished=$false;TradingReady=$false;DurableWrites=0;MutatingCalls=0}
}
function Check([object]$value,[int]$exitCode,[bool]$accept,[string]$name) {
    $json=if($value -is [string]){$value}else{$value|ConvertTo-Json -Depth 8 -Compress}
    $accepted=$false
    try{
        $safe=ConvertTo-SafeAccountRecovery $json $exitCode ([DateTimeOffset]::UtcNow.AddSeconds(-1));$accepted=$true
        if(($safe|ConvertTo-Json -Depth 8 -Compress).Contains('synthetic-private-leak')){throw 'PRIVATE_OUTPUT'}
    }catch{if($accepted){throw 'VALIDATOR_LEAKED'}}
    if($accepted -ne $accept){throw ('DIAGNOSTICS_CASE_FAILED: '+$name)}
    $script:passed++
}
Check (Fixture) 0 $true 'completed-blocked'
$v=Fixture;$v.ScopedAccountObservationsValidated=$false;$v.AcceptedPositionRows=0;$v.BalanceObserved=$false
$v.AccountProblems=@('ReadFailed');$v.Blockers+=@('AccountObservationsUnavailable','AccountReadFailed')
Check $v 0 $true 'account-unavailable'
$v=Fixture;$v.Status='RECOVERY_ACCOUNT_ASSESSMENT_REFUSED';$v.Stage='Input';$v.MetadataValidated=$false;$v.DatabaseUsed=$false;$v.SelectOnly=$false
$v.AssessmentCompleted=$false;$v.RequestedInstruments=0;$v.ScopedAccountObservationsValidated=$false;$v.AcceptedPositionRows=0;$v.BalanceObserved=$false
$v.RetainedFacts=0;$v.KnownOrders=0;$v.RetentionExpiredOrders=0;$v.RetainedTerminalOrdersValidated=0;$v.FromUtc=$null;$v.ThroughUtc=$null;$v.Blockers=@()
Check $v 1 $true 'input-refused'
foreach($mode in @('extra','problem-text','blocker-text','nested','ready','writes','scope','policy','schema','count-string','bool-string',
    'missing','missing-gate','empty-manifest','too-many-rows','too-many-addressed','failed-exit','false-completion','future-time','stale-time','large','invalid-json')){
    $v=Fixture;$exitCode=0
    switch($mode){
        'extra' {$v['NativeAccountId']='synthetic-private-leak'}
        'problem-text' {$v.AccountProblems=@('synthetic-private-leak')}
        'blocker-text' {$v.Blockers+=@('synthetic-private-leak')}
        'nested' {$v.Blockers=@(@{private='synthetic-private-leak'})}
        'ready' {$v.AccountReady=$true}
        'writes' {$v.DurableWrites=1}
        'scope' {$v.Currency='BTC'}
        'policy' {$v.ManifestPolicy='EthPerpetualV1'}
        'schema' {$v.SchemaVersion=1}
        'count-string' {$v.RetainedFacts='synthetic-private-leak'}
        'bool-string' {$v.SelectOnly='true'}
        'missing' {$v.Remove('Blockers')}
        'missing-gate' {$v.Blockers=@('BufferedReplayUnavailable')}
        'empty-manifest' {$v.RequestedInstruments=0}
        'too-many-rows' {$v.AcceptedPositionRows=14}
        'too-many-addressed' {$v.AddressedOrders=33}
        'failed-exit' {$exitCode=1}
        'false-completion' {$v.AssessmentCompleted=$false}
        'future-time' {$v.AssessedAtUtc=[DateTimeOffset]::UtcNow.AddMinutes(1).ToString('o')}
        'stale-time' {$v.AssessedAtUtc=[DateTimeOffset]::UtcNow.AddMinutes(-1).ToString('o')}
        'large' {$v='x'*16385}
        'invalid-json' {$v='{synthetic-private-leak'}
    }
    Check $v $exitCode $false $mode
}
$v=Fixture;$v.RetainedTerminalOrdersValidated=1
Check $v 0 $true 'retained-terminal-validated'
$v.RetainedTerminalOrdersValidated=2
Check $v 0 $false 'retained-terminal-overcount'
$v=Fixture;$v.RetainedTerminalOrdersValidated='synthetic-private-leak'
Check $v 0 $false 'retained-terminal-type'
$v=Fixture;$v.RetainedTerminalOrdersValidated=0
Check $v 0 $false 'missing-terminal-blockers'
$v.Blockers+=@('KnownOrderRetentionExpired','KnownOrderUnavailable','RetainedTerminalEvidenceInsufficient')
Check $v 0 $true 'unresolved-terminal-blockers'
[ordered]@{Status='PASS';Cases=$script:passed;VaultUsed=$false;VenueUsed=$false}|ConvertTo-Json -Compress
