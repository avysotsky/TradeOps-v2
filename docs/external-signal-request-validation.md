# External signal request validation

`POST /api/signals` validates malformed execution instructions before creating a signal audit row or invoking execution/risk services.

## HTTP semantics

- malformed request => **400 Bad Request**
- valid request rejected by trading/risk controls => existing **422 Unprocessable Entity**
- reused `SignalId` with conflicting immutable execution payload => existing **409 Conflict**
- accepted execution => **200 OK**

## Validated input

- `Symbol` is required after trimming and must not exceed 50 characters.
- `Side` must be a defined `OrderSide`.
- `Quantity` must be greater than zero.
- optional `RiskPercent` must be greater than zero and at most 100.
- optional `StopLoss` and `TakeProfit` must be greater than zero.
- optional `Source` must not exceed 100 characters.
- caller-supplied `SignalId` must not be `Guid.Empty`.

## PostgreSQL precision safety

TradeOps rejects decimal input that cannot be persisted without changing its value:

- `Quantity`, `StopLoss`, `TakeProfit`: PostgreSQL `numeric(28,12)`; maximum 12 decimal places and magnitude below 10^16.
- `RiskPercent`: PostgreSQL `numeric(18,8)`; maximum 8 decimal places.

This is also an idempotency requirement. Silent database rounding could otherwise make an exact retry compare differently from its persisted signal payload.

## Persistence invariant

A request rejected with HTTP 400 must create none of:

- `TradingSignals`
- `TradingSignalOutcomeEvents`
- `Orders`

Risk rejection remains a persisted business/execution outcome and therefore continues to create the signal audit plus Received/Rejected outcome history.
