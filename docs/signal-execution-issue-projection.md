# Current signal execution issue projection

TradeOps keeps SignalOutcome limited to the established lifecycle meanings: Received, Accepted, and Rejected. Infrastructure/integrity diagnostics are not represented as fake outcomes.

TradingSignals now has nullable current-diagnostic fields:

- ExecutionIssueCode
- ExecutionIssueMessage
- ExecutionIssueAt

The issue code introduced in this version is ClientOrderIdConflict.

When deterministic local-order identity validation fails after the signal audit already exists, TradeOps keeps the current Outcome unchanged, persists the issue, appends no fake Accepted/Rejected outcome transition, and returns HTTP 409.

For a new unresolved signal this normally means Outcome=Received plus ExecutionIssueCode=ClientOrderIdConflict.

If the conflict is corrected and the signal safely resumes, the current issue fields are cleared. A real Accepted or risk-Rejected transition is then persisted normally.

For an already Accepted signal whose linked order becomes inconsistent, the historical Accepted outcome remains. The current issue is persisted and retry returns 409. Once identity is consistent again, a verified retry clears the issue without fabricating a new outcome transition.

These fields are a current projection, not append-only issue history. TradingSignalOutcomeEvents remains outcome-transition history only.
