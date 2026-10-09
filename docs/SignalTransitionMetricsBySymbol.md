# Signal transition metrics by symbol

TradeOps exposes bounded signal-outcome transition metrics grouped by symbol without changing the established execution metrics contracts.

## Endpoint

```text
GET /api/metrics/signal-transitions/by-symbol?from=...&to=...&limit=...
```

Required:

- `from`
- `to`

Optional:

- `limit` — default `20`, minimum `1`, maximum `100`

The interval uses `[from,to)` semantics and timestamps are normalized to UTC.

## Time axis and source

The query counts only persisted `TradingSignalOutcomeEvents` whose `OccurredAt` is inside the requested window.

It joins each event to its parent `TradingSignals` row only to obtain `Symbol`.

It does not use `TradingSignals.CreatedAt` as the transition time and does not reconstruct historical transitions from the current `TradingSignals.Outcome` projection.

## Per-symbol response

Each returned symbol contains:

- `symbol`
- `totalTransitions`
- `receivedTransitions`
- `acceptedTransitions`
- `rejectedTransitions`

Ordering is deterministic:

1. `totalTransitions` descending;
2. `symbol` ascending.

The repository fetches `limit + 1` rows. If an extra row exists, `isTruncated` is `true` and only the requested number of symbols is returned.

## Historical coverage limitation

Outcome-transition history began in v1.1.2.12 without synthetic backfill. Legacy signals can therefore have current terminal outcomes while having no persisted historical outcome events.

Symbols with no persisted events in the requested window do not appear. Their absence does not prove that no transitions occurred before truthful transition-history persistence existed.

Do not fabricate legacy transition timestamps or counts from current signal projections.

## Separation from other metrics

This endpoint is separate from:

- `/api/metrics/execution/by-symbol`, which preserves the established execution-metrics semantics;
- `/api/metrics/signal-transitions/window`;
- `/api/metrics/signal-transitions/series`.

No execution/trading behavior depends on this read-only aggregation.
