# Signal transition metrics

TradeOps exposes signal transition metrics separately from the existing execution metrics surfaces.

## Endpoint

```text
GET /api/metrics/signal-transitions/window?from=...&to=...&symbol=...
```

`from` and `to` are required timestamps. The interval uses `[from, to)` semantics. `symbol` is optional and is normalized to uppercase.

The response contains:

- `generatedAt`
- `fromInclusive`
- `toExclusive`
- `symbol`
- `receivedTransitions`
- `acceptedTransitions`
- `rejectedTransitions`

## Time axis

This endpoint selects persisted `TradingSignalOutcomeEvents` by `OccurredAt`.

It does not select signals by `TradingSignals.CreatedAt`, and it does not derive historical transitions from the current `TradingSignals.Outcome` projection.

This is intentionally different from the established execution metrics endpoints:

- `/api/metrics/execution/window`
- `/api/metrics/execution/series`
- `/api/metrics/execution/by-symbol`

Those existing endpoints retain their current signal-creation/current-outcome semantics.

## Historical coverage limitation

Outcome history was introduced in v1.1.2.12 without synthetic backfill.

Signals processed before that persistence model existed can have no transition rows even when their current projection is `Accepted` or `Rejected`. Therefore these transition metrics are complete only for transitions that were truthfully persisted to `TradingSignalOutcomeEvents`.

Do not interpret a zero count for an old period as proof that no signal transitions occurred, and do not fabricate transition timestamps from current signal state.
