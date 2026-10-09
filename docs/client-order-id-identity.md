# Deterministic ClientOrderId execution identity

TradeOps derives a deterministic local/exchange client order id from `SignalId`:

`trd-{signalId:N}`

Idempotency is safe only when an existing order under that id represents the same execution intent.

## Identity fields

Before TradeOps reuses an existing local order it now verifies:

- `Symbol`
- `Side`
- `OrderType` — external trading signals currently require `Market`
- `RequestedQuantity`

The check applies to:

1. an order already present when `OrderManager` starts;
2. the persisted winner after a local-order unique-key insert race;
3. the linked local order returned for an already Accepted signal retry.

## Conflict behavior

A mismatch throws `ClientOrderIdConflictException` and `POST /api/signals` returns HTTP **409 Conflict** using the existing conflict response shape:

- `signalId`
- `message`
- `conflictingFields`

No exchange placement occurs on the conflicting path.

## Signal audit state

For a newly received signal, the initial signal audit is persisted before `OrderManager` executes.

If a deterministic local-order identity conflict is discovered after that point, TradeOps intentionally leaves the signal at:

`Outcome = Received`

with only its initial Received outcome-history event.

That state means execution is unresolved and was not finalized as Accepted or Rejected. A retry re-enters execution and returns the same 409 while the conflicting local-order condition remains.

TradeOps does not mark the signal Accepted, because the existing order has not been proven to represent the signal. It also does not mark it Rejected as a risk decision, because this is an integrity conflict rather than a trading/risk rejection.

For an older already-Accepted signal whose linked order projection no longer matches the persisted signal payload, the retry is blocked with 409 rather than silently returning the inconsistent order. Historical Accepted state is not rewritten.

## Exact existing-order recovery

If the deterministic local order exists and all immutable execution fields match, existing idempotent behavior is preserved:

- the local order is reused;
- risk evaluation is not repeated;
- no second exchange placement is made;
- the signal can finalize as Accepted and link to that order.
