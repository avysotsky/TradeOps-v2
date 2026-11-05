# B4 — retained terminal order evidence and quantity reconciliation

This policy determines when a known historical terminal order has sufficient
retained evidence to avoid requiring a new addressed order-state response. It does
not establish current venue state, outage/account-wide coverage, post-trade correction
completeness, a coherent cut or account READY.

## Inputs and authority

Application `DeribitRetainedTerminalEvidence.Assess` takes the actual retained
snapshot, the known-order selection bound to that snapshot, the validated current
query batch and assessment time. No caller-provided "ledger complete" or reusable
proof object is accepted by the gate. Scope, snapshot ID, current epoch, UTC bounds,
source set and collection sizes are checked. The snapshot must have no problems,
only accepted observations and a canonical distinct-fact count matching its declared
count. Current query facts must bind to the selected instrument/currency and epoch.

[Deribit documents](https://docs.deribit.com/api-reference/trading/private-get_order_state)
the 30-minute closed-order recency limit and the native filled-amount field. The
policy below is an application sufficiency rule over recorded native facts, not a
claim that the endpoint supplies an immutable global snapshot or that a missing
lookup confirms cancellation.

## Sufficient historical evidence

For each selected native order inside its full account scope:

1. The selected seed is an actual latest retained report, explicitly cancelled,
   filled or rejected, not later than the query cutoff. An empty venue response or
   missing lookup never creates that report.
2. Report terms/native units agree. Conflicting same-time reports, cumulative
   regression, reopening or changes to an already terminal state/quantity refuse
   proof. Cumulative filled quantity must be explicitly present. Filled requires
   filled=order amount; rejected requires filled=0.
3. All retained and current executions for the order are deduplicated using the
   existing per-currency trade + own-order-leg canonical identity. Quantity, price,
   fee or other immutable-payload collisions refuse proof; executions must include
   their price and fee details and match side/instrument/native units.
4. Execution venue times and all compared reports must not extend beyond the
   selected terminal report. Late **receipt** of an older fill is permitted; a fill
   with a later venue time is not reconciled away.
5. The checked sum of distinct native execution amounts must equal the explicit
   terminal cumulative quantity. Missing fills, overfill and decimal overflow fail.
   Cumulative quantity is never transformed into invented execution records.

Thus an explicit cancelled/rejected zero-fill report with no contradictory execution
can be sufficient. A partially filled cancelled order requires its actual fills;
current trade facts may complete a missing retained quantity, without implying a
complete venue history or filling unrelated ledger gaps. Other currencies/accounts/
own order legs never supply the selected order's executions.

The output is scoped to the supplied snapshot/current query evidence. Unobserved
post-trade corrections or buffered events not yet applied cannot be ruled out by
this arithmetic comparison. Those remain under the unchanged history/coherent-cut/
execution-ledger blockers. The existing order reducer's fresh-evidence requirements
are not changed or bypassed, and this output is not a publication/send permission.

## Gate behavior and diagnostics compatibility

The gate derives the assessment itself after validating retained and current facts.
For an expired terminal reference with sufficient evidence, it increments
RetainedTerminalOrdersValidated and does not add KnownOrderRetentionExpired or
KnownOrderUnavailable solely because the endpoint cannot serve an old record.
RetentionExpiredOrders still records how many references exceeded recency; it is
an observation count, not by itself a blocker.

Insufficient evidence keeps both blockers and adds RetainedTerminalEvidenceInsufficient.
An unrelated valid terminal cannot clear another order's unresolved state. No old
terminal lookup is resent. Current available compatible reports keep their existing
path; open/triggered/untriggered orders still require current evidence. The global
HistoricalUnfilledCoverageGap, OrderHistoryIncomplete, CoherentCutUnavailable,
BufferedReplayUnavailable, ExecutionLedgerCoverageUnproven and account-wide/account
coherence gates are unaffected by the historical sufficiency decision.

The unified diagnostics DTO advances explicitly to **SchemaVersion=2** and adds the
validated-terminal count. The wrapper enforces count ≤ expired count and requires
the retention/unavailable/insufficient blockers for unresolved expired references.
Previously saved v1 reports remain historical; they are not accepted as current v2
payloads. Existing enum numeric values are preserved by appending the new blocker.
Durable event v2/observation v1, canonical encoding and DB schemas are unchanged.
