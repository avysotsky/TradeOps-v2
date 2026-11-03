# TO-V2-21 — Durable synthetic scoped order / execution idempotency reservations

**Private repo:** `avysotsky/TradeOps-v2`
**Base:** independently qualified TO-V2-20 exact SHA
`7bb95d26c56a20e9782ce9397de2b49c93064b57`
**Branch:** `TradeOps/to-v2-21-durable-synthetic-scoped-idempotency`
**Status:** Draft. No merge, no published packages, no production DB changes.

## Problem and safety boundary

TO-V2-20 introduced PURE pairwise scoped idempotency decisions, but
these could not arbitrate two concurrent callers or protect against a
process restart. A global `Orders.ClientOrderId` and
`Fills.ExchangeFillId` still blocks using identical external IDs
across real broker accounts. Those legacy indices and the live
signal/worker API MUST NOT be weakened in this workstream.

This milestone creates **two entirely separate synthetic-only durable
reservation tables**, without attaching them to live trading orders,
fills, signals or API routes. All executable acceptance is restricted
to a fresh disposable PostgreSQL 17 `tradeops_api_smoke_*` database
created by the existing Windows -> WSL2 Docker acceptance script.

This is NOT authenticated broker evidence and NEVER a signal/order
placement/retry/cancel authorization. The tables do not imply that any
broker accepted an order. They cannot be used to automatically
resubmit an `Unknown` order. All rows are local synthetic fixtures.

## Critical EF Core isolation correction

The original draft applied reservation entity configurations to the
live `TradeOpsDbContext`; independent WSL2 tests failed. A controlled
source-tree rollback proved the runner itself was healthy
(899/899 PASS), while both a full and a minimal reservation mapping
inside the live model produced `TEST_FAILED`. Those draft commits
are superseded.

The corrected implementation uses `ScopedReservationsDbContext`
with **its own model snapshot and explicitly invoked migration** under
`Persistence/ScopedReservationMigrations`. The live
`TradeOpsDbContext` excludes both reservation configurations from
its assembly scanner and retains **exactly the previously accepted
model and migration history contract**. No API/Worker DI registration
of the new context exists.

Only the disposable-loopback PostgreSQL smoke executable constructs
`ScopedReservationsDbContext`, explicitly calls
`Database.MigrateAsync()`, and then exercises its isolated journal.
This new migration is never auto-run by the production API/Worker
main context. A new xUnit guard checks both that the reservations
model has the exact execution FK and that the main model does not
contain either reservation entity or its migration.

## Tables and invariants

### `SyntheticScopedOrderReservations`

- Primary key: `(ProviderKey, AccountKey, ClientOrderId)`.
- Unique global `LocalOrderId`: a local GUID cannot be rebound to a
  different provider/account/cOID.
- Unique `(ProviderKey, AccountKey, BrokerOrderId)`: one broker order
  identity per scoped local reservation.
- Immutable terms stored independently:
  `InstrumentKey, Side, OrderType, RequestedQuantity, Price`.
- SQL checks restrict `Classification=SyntheticOnly` and only the
  synthetic `Mock`/`IbkrOffline` instrument namespaces.
- **BrokerOrderId is required in THIS experimental ledger.**
  An unbound client order returns `RequiresProof`. First broker-ID
  binding must be separately authenticated and durable before future
  production orders can use this concept.

### `SyntheticScopedExecutionReservations`

- Primary key: `(ProviderKey, AccountKey, ExecutionId)`.
  Thus *the same external execution ID* is valid in DIFFERENT scoped
  accounts, but never twice in one account with conflicting payload.
- Composite FIVE-COLUMN SQL foreign key to the precise parent order:
  `ProviderKey + AccountKey + ClientOrderId + LocalOrderId + BrokerOrderId`.
  This prevents misassigning a partial fill to a different account,
  client ID or broker order ID, even if an application-level check fails.
- Immutable execution terms: `Quantity, Price, Fee, FeeCurrency`;
  SQL precision is compatible with `numeric(28,12)` and the pure
  TO-V2-20 validation.
- Both tables have SQL-level `SyntheticOnly` checks.
- **No FK to live `Orders` or `Fills`** because those legacy tables
  cannot yet represent duplicate external IDs across accounts. A
  scoped reservation does not create an active local order.

## Transactional journal

`EfSyntheticScopedReservationJournal` implements two opt-in,
**non-DI-registered** methods:

- `ReserveOrderAsync(ScopedOrderFingerprint)`
- `ReserveExecutionAsync(ScopedExecutionFingerprint)`

Steps for both: strict TO-V2-20 input validation -> PostgreSQL
ReadCommitted transaction -> insert with `ON CONFLICT DO NOTHING`
-> if inserted, COMMIT -> otherwise read the immutable conflicting
record, classify `EquivalentReplay` or `IdentityConflict`. An
execution first verifies its parent reservation and the SQL FK
independently validates the same five-part binding.

The outcome `Inserted` means only *a synthetic local reservation row
was created*, NEVER that a broker order was placed, a live `Fill` was
applied or the TradeOps signal was accepted. A missing broker binding
returns `RequiresProof`; conflicts never overwrite previous rows.

For simultaneous callers, PostgreSQL's unique index arbiter provides
the durable single-winner. On process restart, the same
fingerprints replay against stored immutable rows. For completely
identical concurrent retries, exactly one can insert, the other
returns `EquivalentReplay`. For conflicting concurrent payloads on
one key, one inserts, the other returns `IdentityConflict`. The
winner's particular payload is intentionally not predetermined.

The policy is still **NOT durable multi-broker trading idempotency**:
no account authentication, broker-source verification, observation
sequence and digest or execution ledger import is connected to it.

## Real PostgreSQL executable verification

`tools/TradeOps.ScopedReservationsPostgresSmoke` is an independent
.NET 10 executable referencing Infrastructure. It rejects invocation
unless:

- `TRADEOPS_SYNTHETIC_RESERVATION_CONFIRM` equals
  `DISPOSABLE_SCOPED_RESERVATION_PG_ONLY`.
- The connection host is exactly `127.0.0.1`.
- The database name begins with `tradeops_api_smoke_` and has a
  safe strictly limited character set.
- No arbitrary search_path or libpq options are set.

It runs against the already-bootstrapped **real disposable API
PostgreSQL 17** only, after Api/Worker are stopped. The test-only
`ScopedReservationsDbContext` explicitly applies its migration to
that database, separate from the main API migration context, then tests:

1. New migration is present; both reservation tables start empty.
2. Two Mock accounts use identical external `ClientOrderId`,
   `BrokerOrderId` and `ExecutionId`, but different local GUIDs.
3. A third `IbkrOffline` synthetic provider uses those same IDs,
   proving account AND venue scoping.
4. Exact durable order/execution replay after creating a new DbContext.
5. Changed immutable order/fill terms, wrong GUID, cross-account reuse,
   reused scoped broker ID and wrong parent binding fail closed.
6. Two concurrent different-payload order reservations => one winner,
   one conflict. Two identical retries => one winner and one replay.
7. Two concurrent different-payload execution reservations => one
   winner, one conflict; verified counts remain stable.
8. Legacy `Orders` remains exactly one, `Fills` zero and old
   `BrokerOrderIdentities` sidecar empty.

Fixed required marker:

```text
TRADEOPS SCOPED RESERVATION SMOKE PASS orders=6 executions=4 concurrency=PASS scope=PASS replay=PASS conflicts=PASS legacy_unchanged=true broker_requests=0
```

The existing real acceptance script still requires all prior
IBKR journal, HTTP API initial/restart, Worker synthetic canary,
SQL and cleanup markers, and now also this marker. The owner
Windows launcher is unchanged and requires exact `-Branch` and
`-ExpectedHead`. The temporary DB/container is destroyed by
the existing script's cleanup trap.

## Qualification

**Gate A:** independent WSL2 exact new SHA, NuGet restore PASS,
Release build PASS, all xUnit PASS, 0 skipped, 6/6 cleanup.

**Gate B:** separate owner-run real PostgreSQL 17 acceptance on
that EXACT HEAD. Do not run Gate B until Gate A passes and source
and smoke requirements have been reviewed. Do not reuse TO-V2-19's
runtime evidence.

### Future production migration (explicitly not implemented)

A new owner-approved integration must replace every unscoped
repository query, signal ingress receipt, operator cancellation,
Worker reconciliation and fill import path with verified
provider/account/fully-qualified-instrument IDs. A single durable
transaction must reserve the scoped identity AND persist the
real Order/Fill or signal outcome, with durable observation digest
and optimistic versioning. Only then can one consider replacing
legacy global unique constraints.

There is no automatic move of legacy Order IDs, cOIDs or fills;
old rows remain unscoped and must be quarantined from real
multi-provider operations. Real Interactive Brokers conids and
authenticated execution IDs require their own provider adaptor
and Paper-account qualification.
