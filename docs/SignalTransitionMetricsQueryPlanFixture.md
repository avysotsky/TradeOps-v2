# Signal transition metrics query-plan fixture

This fixture protects the `GET /api/metrics/signal-transitions/window` access path introduced in v1.1.2.13.

## Representative dataset

The PostgreSQL-backed tests create an isolated database, apply the real migrations, and load:

- 40,000 `TradingSignals`;
- 80,000 `TradingSignalOutcomeEvents`;
- eight symbols;
- roughly 28 days of deterministic event-time distribution;
- one `Received` and one terminal outcome event per signal.

`ANALYZE` runs before plans are inspected.

This is a controlled CI fixture for detecting access-path regressions. It is not a production traffic model or latency SLO.

## Query shapes

The fixture checks the two window forms used by signal-transition metrics:

1. unscoped `[from,to)` aggregation using `TradingSignalOutcomeEvents.OccurredAt`;
2. symbol-scoped `[from,to)` aggregation joined to `TradingSignals.Symbol`.

The v1.1.2.12 history index `(TradingSignalId, OccurredAt)` is appropriate for per-signal history reads, but its leading key does not provide the selective access path required by the unscoped event-time window.

v1.1.2.14 therefore adds the physical PostgreSQL index:

`IX_TradingSignalOutcomeEvents_OccurredAt`

The index is migration-managed because it is a physical query optimization for this read surface; no domain or execution semantics depend on it.

## Regression guard

`SignalTransitionMetricsIndexPostgresTests` verifies on the representative fixture that:

- the real migration creates `IX_TradingSignalOutcomeEvents_OccurredAt`;
- the selective unscoped plan uses that index;
- neither the unscoped nor symbol-scoped representative plan performs a sequential scan of `TradingSignalOutcomeEvents`.

`SignalTransitionMetricsQueryPlanPostgresTests` continues to record median repository timings and `EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)` observations for CI diagnostics.

Do not treat those timing observations as a production latency guarantee.
