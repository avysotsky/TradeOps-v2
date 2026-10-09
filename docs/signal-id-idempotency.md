# SignalId idempotency and conflict semantics

`POST /api/signals` accepts an optional caller-supplied `signalId`.

A `signalId` identifies one immutable execution intent.

## Exact retry

If the same `signalId` is submitted again with the same execution payload, TradeOps keeps the existing idempotent behavior:

- no second logical signal is created;
- no second exchange placement is performed;
- a persisted Accepted signal reuses its linked local order;
- a persisted Rejected signal reuses its persisted rejection;
- a persisted Received signal resumes the existing logical execution.

`Source` and request receipt time are metadata and are not part of execution identity.

## Conflicting retry

If the same `signalId` is reused with a different execution payload, TradeOps returns HTTP 409 Conflict and does not continue execution.

Execution identity includes:

- Symbol
- Side
- SignalType
- RequestedQuantity
- RiskPercent
- StopLoss
- TakeProfit

The conflict response includes the signal id and the names of conflicting fields.

This check also applies after a concurrent unique-key insert race: the persisted winner must match the incoming execution intent before it can be reused.

## Safety rationale

Deterministic client-order identity is derived from `signalId`. Silently accepting a changed execution payload under an existing `signalId` can make the caller believe new instructions were executed when TradeOps actually reused an older order. Treating that condition as an explicit conflict preserves idempotency without conflating different execution intents.

This feature does not change ambiguous placement recovery, reconciliation, cancellation, exchange selection, or risk rules.
