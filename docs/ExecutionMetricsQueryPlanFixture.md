# Execution metrics query-plan fixture

This document describes the deterministic PostgreSQL performance-validation fixture introduced in TradeOps v1.1.2.11.

## Purpose

The fixture validates the existing execution-metrics read paths without changing their public API contracts:

- `GET /api/metrics/execution/window`
- `GET /api/metrics/execution/series`
- `GET /api/metrics/execution/by-symbol`

It is deliberately a repeatable engineering fixture, not a claim about production-scale capacity and not a latency SLO.

## Representative persisted volume

The PostgreSQL-backed test creates an isolated database, applies the real EF Core migrations, then inserts deterministic data with SQL `generate_series`:

- 20,000 `Orders`
- 40,000 `TradingSignals`
- 60,000 `OrderLifecycleEvents`
- 20,000 `Fills`

That is 140,000 persisted execution facts plus the 20,000 parent orders.

Facts are distributed across approximately 28 days and eight symbols:

- BTCUSDT
- ETHUSDT
- SOLUSDT
- XRPUSDT
- ADAUSDT
- DOGEUSDT
- LINKUSDT
- AVAXUSDT

After loading the fixture, PostgreSQL `ANALYZE` is run for the four involved tables so the planner uses current statistics.

## Query shapes validated

The fixture measures repository calls after one warm-up execution:

- symbol-scoped 1-hour execution window;
- symbol-scoped 6-hour execution series with `5m` buckets;
- 24-hour bounded by-symbol breakdown with `limit=50`.

Five measured calls are executed for each repository path and the median elapsed time is recorded.

The fixture also executes `EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)` for:

- window signal aggregation;
- window lifecycle aggregation;
- window distinct `ordersTouched` aggregation;
- window fill count;
- symbol-scoped fixed-bucket series aggregation;
- bounded by-symbol aggregation.

## Structural regression rule

For the deliberately selective fixture windows, a sequential scan of any execution fact table is treated as a plan regression:

- `TradingSignals`
- `OrderLifecycleEvents`
- `Fills`

Sequential scans of other relations are not categorically rejected because PostgreSQL can legitimately choose them for small parent/reference inputs or hash joins.

The test records all index names used by each plan. It does not force planner settings such as `enable_seqscan=off`; the objective is to observe the plan PostgreSQL 16 actually selects with the real migrated schema and statistics.

## Existing indexes under evaluation

The fixture evaluates the access paths already introduced before v1.1.2.11, including:

- `IX_TradingSignals_CreatedAt`
- `IX_TradingSignals_Symbol_CreatedAt`
- `IX_OrderLifecycleEvents_OccurredAt`
- `IX_OrderLifecycleEvents_OrderId_OccurredAt`
- `IX_Fills_FilledAt`
- `IX_Fills_OrderId_FilledAt`
- `IX_Orders_Symbol`

v1.1.2.11 must not add an index merely because one looks plausible. A new index is justified only if the representative plans demonstrate a missing or materially poor access path.

## Baseline output

On GitHub Actions, the test appends a Markdown report to `GITHUB_STEP_SUMMARY`. The report contains:

- PostgreSQL server version;
- fixture row counts;
- repository median latency observations;
- plan planning/execution time;
- root actual rows;
- shared hit/read blocks;
- indexes selected by the planner;
- any sequential scans of execution fact tables.

The same report is written to `metrics-query-plan-report.md` in `GITHUB_WORKSPACE` for inspection during that CI run.

These measurements are environment-specific observations. They are intended to make future regressions visible and comparable; they are not production guarantees.

## Local execution

The test runs automatically in GitHub Actions using the existing PostgreSQL 16 service.

For optional local execution, set:

`TRADEOPS_TEST_POSTGRES_ADMIN`

to an administrative PostgreSQL connection string. The test creates and drops its own temporary database. Without that variable, the performance fixture is skipped outside GitHub Actions so ordinary local unit-test runs do not require PostgreSQL.
