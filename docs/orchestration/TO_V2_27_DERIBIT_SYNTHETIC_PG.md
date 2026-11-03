# TO-V2-27 — Synthetic scoped Deribit execution evidence persistence

**Base:** owner-qualified TO-V2-26 SHA `8065eb74de5025f9b4d6695ef77bd7f0425c851c`.

Deribit authenticated read-only BTC history in the trailing seven days
returned ZERO execution rows on both historical and recent index queries,
so a real filled order, reconciliation, or persistence import is NOT proven.

## Workstream scope
Introduce `DeribitSyntheticExecutionJournal` as an opt-in synthetic-only
PostgreSQL journal that CANNOT connect outside disposable local databases:
host 127.0.0.1, DB name `tradeops_api_smoke_*`, no custom search_path
or connection options. It has no DI registrations, no trading or API client,
no wallet access and no path from live account data.

Single standalone table `SyntheticDeribitExecutionEvidence` with composite
unique primary key (`ProviderKey, AccountKey, ExecutionId`).
Provider is fixed `DeribitSynthetic`, AccountKey must begin
`synthetic-account-`. Immutable fields:
broker order ID, instrument, direction, native amount (do not rename
to contracts), price, signed fee, fee currency, timestamp milliseconds,
optional native contracts. These are stored as PostgreSQL `numeric`,
not rounded to a fixed 12-place scale.

`INSERT ... ON CONFLICT DO NOTHING` followed by immutable row comparison
implements idempotent replay vs conflicting evidence. Identical trade ID
is permitted in a distinct synthetic account. This DOES NOT prove the
scope or authenticity of any real Deribit account, nor authorize a
trade/order submission.

Live `TradeOpsDbContext` and EF migrations remain completely unchanged;
`Orders` and `Fills` plus legacy global uniqueness remain unchanged.
The journal creates only its own isolated table on an explicitly permitted
disposable test database, with checks and unique key.

## Acceptance
- Independent WSL2 exact HEAD restore/build/xUnit 0 failed/skipped,
  cleanup 6/6 (no external Deribit requests).
- Separate owner opt-in disposable PostgreSQL smoke
  `tools/TradeOps.DeribitSyntheticExecutionPgSmoke`.
  Requires `TRADEOPS_SYNTHETIC_DERIBIT_CONFIRM=
  DISPOSABLE_DERIBIT_EXECUTION_PG_ONLY` and
  `TRADEOPS_SYNTHETIC_DERIBIT_PG` connection string. Reject live/prod DB.
  Smoke inserts synthetic account A/B same external ID, replay, conflicting
  price/order/fee, concurrency and durability across instances.
  No real account API keys or real execution data.
- This synthetic journal is NOT production executed-fill persistence.
  Later stages require authenticated provider+account binding, handling of
  historical index completeness and safe correlation with orders.
- Do not auto-launch smoke or merge PR. No market orders/cancels/transfers.
