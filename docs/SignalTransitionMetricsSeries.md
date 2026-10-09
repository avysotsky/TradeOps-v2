# Signal transition series metrics

TradeOps exposes bucketed signal-outcome transition metrics separately from the existing execution-series contract.

## Endpoint

```text
GET /api/metrics/signal-transitions/series?from=...&to=...&bucket=...&symbol=...
```

Required:

- `from`
- `to`
- `bucket`

Optional:

- `symbol`

The interval uses `[from,to)` semantics. Timestamps are normalized to UTC. `symbol`, when supplied, is trimmed and normalized to uppercase.

Supported buckets:

- `1m`
- `5m`
- `15m`
- `1h`
- `1d`

The maximum returned series length is 500 buckets. Larger requests return HTTP 400.

## Time axis

Every transition count is selected and bucketed only by:

`TradingSignalOutcomeEvents.OccurredAt`

The series never derives transition time from `TradingSignals.CreatedAt` or the current `TradingSignals.Outcome` projection.

## Stable buckets

PostgreSQL generates the complete bucket range and left-joins persisted transition counts. Therefore buckets with no transitions are returned explicitly with zero counts.

The first and last bucket boundaries are clipped to the requested `[from,to)` interval while aggregation remains aligned to the UTC epoch, matching the established execution-series bucket alignment convention.

Each bucket contains:

- `fromInclusive`
- `toExclusive`
- `receivedTransitions`
- `acceptedTransitions`
- `rejectedTransitions`

## Symbol scope

When `symbol` is supplied, outcome events are joined to their parent `TradingSignals` row and filtered by `TradingSignals.Symbol`.

## Historical coverage limitation

Outcome-transition history was introduced in v1.1.2.12 without synthetic backfill. Pre-v1.1.2.12 signals can therefore have current terminal outcomes without persisted historical events.

A zero bucket means no persisted transition events exist for that bucket and scope. It does not prove that no transition occurred before truthful history persistence existed.

Do not fabricate legacy transition timestamps.

## Separation from execution metrics

This endpoint does not change:

- `/api/metrics/execution/window`
- `/api/metrics/execution/series`
- `/api/metrics/execution/by-symbol`

Those routes retain their established signal-creation/current-outcome semantics.
