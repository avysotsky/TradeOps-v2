# TO-V2-19 — Additive synthetic broker/account/instrument identity persistence

**Repository:** PRIVATE `avysotsky/TradeOps-v2`
**Parent:** TO-V2-18 exact real-runtime-qualified HEAD
`c5f1b886ff8ae79cec8f05c9f365ae7d5c7e0162`
**Branch:** `TradeOps/to-v2-19-broker-order-identity-persistence`
**Safety:** Draft only. No production migration, merge, account connection,
real broker request or trading operation.

## Problem: legacy global identities are not multi-venue-safe

Current `Orders` contains global `ClientOrderId`,
`ExchangeOrderId` and ticker `Symbol`. None of these proves
the venue, account or qualified contract (notably IBKR `conid`
+ exchange + currency + security type). The existing global unique
indices on `Orders.ClientOrderId` and `Fills.ExchangeFillId`
are also **not suitable as final multi-venue uniqueness constraints**:
two accounts can share the same external IDs.

However, changing these indices, legacy HTTP idempotency, the Worker,
the original order table and execution persistence in the same step
would be risky. In particular, guessing `ProviderKey` or `AccountKey`
for historical rows from their ticker/order ID is **forbidden**.

## Implemented: additive sidecar, no backfill

A new EF Core entity and real PostgreSQL table
`BrokerOrderIdentities` provide explicit scope without mutating
or re-keying existing `Orders`:

| Column | Type | Constraint |
| --- | --- | --- |
| `OrderId` | uuid | PRIMARY KEY, FK to `Orders.Id`, RESTRICT delete |
| `ProviderKey` | varchar(64) | Mock or IbkrOffline only |
| `AccountKey` | varchar(128) | nonblank and trimmed |
| `InstrumentKey` | varchar(192) | provider-specific SYNTHETIC namespace only |
| `ClientOrderId` | varchar(100) | nonblank; must match Orders in read projection |
| `BrokerOrderId` | varchar(100), nullable | if known, must match Orders in read projection |
| `Classification` | varchar(32) | forced by database CHECK to `SyntheticOnly` |
| `RecordedAt` | timestamptz | mandatory |

Database CHECK constraints explicitly prohibit a self-asserted
`BrokerVerified` classification, an unqualified IBKR instrument
(`AAPL`), real Paper/live `ProviderKey`, or blank account key.
A `Mock` record requires an instrument key beginning `MOCK:`;
an `IbkrOffline` record requires `SYNTHETIC-IBKR-SYMBOL:`.
**These synthetic names are not IBKR conids or authenticated proof.**

Two scoped UNIQUE indices:

- `(ProviderKey, AccountKey, ClientOrderId)`
- `(ProviderKey, AccountKey, BrokerOrderId)` with non-null filter

`OrderId` has at most one scope row. No legacy order receives
a row automatically. Scope must be explicitly supplied by the
owner's isolated synthetic acceptance fixture; no production
register/write interface is added.

The migration ID is
`20261022235900_AddSyntheticBrokerOrderIdentities`.
EF Core model snapshot is updated. Migration `Up` only creates the
sidecar table and its scoped constraints/indices; `Down` drops
only that table. There is **no** legacy `UPDATE`, `ALTER` or
`DROP INDEX`.

## New read-only projection (not runtime registered)

`EfSyntheticScopedOrderReader` queries the real local DB by
`(provider, account)`, joining on immutable `Orders.Id`.
It checks both legacy and scoped cOID/broker order identities,
supports at most 256 rows, and returns only nonterminal
`Submitted`, `Accepted`, `PartiallyFilled`, `Unknown`
local-order candidates.

If no sidecar scope exists, the old order is **not** selected.
Provider/account mismatch, contradictory identity, non-synthetic
classification, invalid inputs and excessive rows fail closed.
The reader uses `AsNoTracking` and provides no save/update API.
It is **not DI-registered** with API/Worker and is not yet used
as a live read-only shadow comparison source.

A synthetic sidecar row is **never authority for actual IBKR
order submission, authenticated account access, risk clearance or
a durable execution ID import**. The future genuine broker adapter
needs an independently authenticated venue/account/contract
binding, native-API coverage, verified source timestamps, durable
observation digests, locked updates and account-scoped execution
indices.

## Real PostgreSQL 17 acceptance

The owner-executed
`scripts/ibkr/to-v2-14-run-real-postgres-acceptance.sh`
runs all existing real PostgreSQL journal, API HTTP, restart,
synthetic Worker canary, SQL and cleanup checks.

After the API and Worker stop, it additionally streams
`scripts/ibkr/to-v2-19-synthetic-order-identity.sql`
into **the disposable API PostgreSQL database only** using
`docker exec -i ... psql -v ON_ERROR_STOP=1`.

The SQL fixture:

1. Proves zero automatic broker-scope rows for the existing HTTP order.
2. Explicitly binds one synthetic Mock account/instrument scope
   within a PostgreSQL transaction and validates the `OrderId` join.
3. Confirms unsupported provider, forged verified classification,
   unqualified instrument, blank account, orphan FK,
   duplicate per-account/cOID and duplicate `OrderId` are rejected.
4. Creates a second disposable synthetic order inside the
   transaction solely for testing uniqueness.
5. Ends with **ROLLBACK** and proves exactly one original order,
   zero remaining sidecar rows, the migration history entry and
   two unique scoped indices.

Mandatory new marker:

```text
TRADEOPS REAL BROKER SCOPE SQL PASS synthetic_only=true legacy_unscoped=true constraints=true rollback=true
```

The complete owner acceptance must still print the original:

```text
TRADEOPS REAL POSTGRES ACCEPTANCE PASS sha=<exact-new-SHA> pg=17 journal=PASS api=PASS restart=PASS worker=PASS cleanup=PASS broker_requests=0
REAL POSTGRES ACCEPTANCE EXIT: 0
```

A **new exact-SHA** WSL2 independent restore/build/unit/cleanup
PASS is required BEFORE owner runtime acceptance. The parent
TO-V2-18 successful real Postgres result cannot qualify this
new migration.

## Outstanding for genuine multi-broker orders

Do not prematurely drop global unique constraints in this PR.
Migration to account-scoped persistent `Orders` and `Fills`
requires a separate compatibility review of all idempotency
paths, signals, APIs, client ID generation, Fills ingestion,
audit queries, PostgreSQL backfills and downstream consumers.
A careful v2 identity migration must define whether old records
remain unscoped and quarantined, support an explicitly authorized
reconciliation process and prove zero duplicate execution imports.

Do not let the shadow engine read live IBKR account data until
these contracts, credentials, conid and concurrency gates exist.

**No PR merge, CI service mutation, NuGet publication or real/Paper
order transport is permitted in TO-V2-19.**
