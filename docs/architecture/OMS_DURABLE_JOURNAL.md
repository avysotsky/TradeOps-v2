# B2-01 — Durable OMS journal, PostgreSQL 17 (store-only v1)

**Status (2026-11-04):** final DB-only review completed for [Issue #67](https://github.com/avysotsky/TradeOps-v2/issues/67), with 23/23 PostgreSQL17 cases and 5/5 contract cases PASS. Decimal-scale replay and restricted application-role acceptance are covered. **NOT an order-sending gateway**. Historical source branch `TradeOps/b2-01-durable-command-journal`; base `TradeOps/b1-01-oms-domain-identity-contracts@a799aec77ef498c88977a3ee6466989c1a0958ee`. See the [final review and legacy integration plan](../orchestration/workstreams/B2_01_FINAL_REVIEW_2026-11-04.md) for exact evidence and limits. B3/B4/B5 and physical-send fencing remain separate gates.

## Non-negotiable boundary

This PR adds only `src/TradeOps.Application/Oms/**`, `src/TradeOps.Infrastructure/Persistence/Oms/**`, **one independent EF migration**, dedicated `OmsJournal*Tests` and this document. It **does not** touch the original `OrderManager`, `OrderStateMachine`, application registration, PostgreSQL legacy `Orders`/`Fills`, exchange clients, credential management, React, CI scripts/workflows, execution flags or public order-entry APIs. There is **no dispatcher, hosted worker, network order transport, order-risk bypass or live exchange call**. Existing MAINNET remains STRICT READ-ONLY.

## Full account scope and identifiers

Every row identity includes **(tenant_id, venue_id, environment, account_id, subaccount_id)** using B1 `AccountKey`. Opaque remote and account labels are case-sensitive; venue ID is B1-canonicalized registry key. `subaccount_id` is NOT NULL: absent subaccount is represented by `''`, distinct from every valid nonempty named subaccount. This avoids PostgreSQL's ordinary multi-null unique index behavior. Subaccount input with an actual empty string fails B1 validation. Explicit enum `OmsEnvironment` maps 1..4. Account fields are NOT logged as secrets but should still be handled as sensitive metadata by downstream observability.

`oms_aggregates` stores the local order aggregate UUID and monotonic version, separate from legacy Order.Id. `oms_intents` stores typed immutable intent ID -> local aggregate, plus provenance. `oms_commands` stores scoped `CommandId`, intent ID, aggregate ID, `ExpectedAggregateVersion` and resulting version, entire immutable JSON payload, schema version `OmsContractSchema.Version`, and SHA-256 digest. `oms_outbox` has exactly one durable row per command. `oms_attempts` records the physical-send boundary (attempt #1 only in v1). `oms_inbox` is a store-only, immutable remote event inbox deduplicated by account scope + adapter-defined execution/order event namespace + opaque ID. `oms_integrity_incidents` stores minimal collision evidence without copying source payload. `oms_account_leases` reserves monotonic DB metadata generation; **not send authorization**.

**Key DDL (exact new tables and indexes in** `src/TradeOps.Infrastructure/Persistence/Migrations/20261025193000_AddOmsDurableJournal.cs`**):**

| Table | Primary/unique key | Additional invariants |
|---|---|---|
| `oms_aggregates` | `(scope..., aggregate_id)` | `version>=0`, trigger permits +1 version updates only |
| `oms_intents` | `(scope..., intent_id)` | FK scope+aggregate, immutable-row trigger |
| `oms_commands` | `(scope..., command_id)`; unique `(scope...,aggregate_id,aggregate_version)` | FKs intent/aggregate, 32-byte SHA256, version > 0, payload immutable trigger |
| `oms_outbox` | `(scope..., command_id)` | FK command, `NotSent -> NeedsRecovery -> Resolved` only |
| `oms_attempts` | `(scope...,command_id,attempt_number)` | FK command, phase transitions checked, no delete |
| `oms_inbox` | `(scope...,event_namespace,event_id)` | append-only, payload SHA256 collision detection; time index |
| `oms_integrity_incidents` | generated bigint `incident_id` | append-only, no payload/credential dump |
| `oms_account_leases` | `(scope...)` | monotonically incremented generation under row lock |

The schema uses `jsonb`, server-side `clock_timestamp()` timestamps (`timestamptz`), simple `bytea` digests, PostgreSQL checks, composite FKs and atomic unique constraints. `OmsJournalCanonical` serializes a fixed-version, fixed-field request (including full scope, IDs and expected version) with deterministic System.Text.Json options and hashes the UTF-8 bytes. Maximum payload 16 KiB. Validation rejects whitespace-only/oversized inputs, invalid enum and GUIDs, non-UTC request event timestamps, nonpositive quantities/prices. The strongly typed payload carries instrument, side, order type, units, quantity, **limit, trigger, time-in-force, reduce-only, post-only, provenance, operator/strategy, risk decision reference and request UTC time**, plus mandatory target client/exchange order reference for Cancel/Replace and optional distinct FIX ClOrdID/OrigClOrdID correlation. Nulls are explicit. No API keys, auth headers or transport serialization strings are accepted. An existing scoped command/event ID is `Duplicate` when its digest matches or its entire versioned JSONB payload is equal. Thus `1` and `1.000` are equal decimal values, while changed parameters remain `Collision`. The first writer's JSON and SHA-256 are never rewritten; existing v1 rows require no migration. See the final review for the numeric replay regression and restricted-role evidence.

## Transactional operations / concurrency

1. `OmsPostgresJournal.PersistAsync` opens one ReadCommitted transaction and first acquires **transaction-scoped account/CommandId and account/IntentId PostgreSQL advisory locks**, in that order. These locks arbitrate simultaneous same-ID submissions so they return deterministic `Duplicate`/`Collision` rather than a unique-index exception; the unique constraints and CAS remain the authoritative correctness controls. It resolves an existing command ID before seeding any aggregate (avoiding ghost rows on collision), checks intent ownership, seeds scoped aggregate if absent, inserts/locks intent under its scoped unique key, **verifies owner AFTER conflicting insert arbitration**, performs `UPDATE ... WHERE version = expected RETURNING version` CAS, inserts command and its outbox row, and commits atomically. `Created`, `Duplicate`, `Collision`, `StaleVersion` are distinct. A failure prior to COMMIT rolls back all tentative changes; stale CAS also rolls back tentative intent/aggregate rows. Collisions write a minimal immutable integrity incident. All SQL values are Npgsql parameters; no concatenated untrusted identifiers.
2. `MarkDispatchStartedAsync` changes `PersistedNotSent -> DispatchStarted`, inserts attempt #1, and changes outbox `NotSent -> NeedsRecovery` **in one committed DB transaction**. The future physical-send gateway must invoke and observe commit **before** any outbound bytes. The v1 repository itself never sends. If it crashes immediately afterward, the pending outbox **cannot** be selected as safely unsent, and a second `MarkDispatchStartedAsync` returns `AlreadyStartedOrResolved` instead of generating a new attempt. Caller **MUST NOT** interpret this as successful execution.
3. `RecordOutcomeAsync` only accepts durable post-dispatch `Acknowledged`, `Rejected`, `Ambiguous`. An ACK means **transport request acknowledgement, never filled/exchange terminal**. Ambiguous remains `NeedsRecovery`. Outbox `Resolved` means command dispatch disposition recorded; filled positions and order truth require B4 reconciliation.
4. `PersistInboxAsync` uses `INSERT ... ON CONFLICT DO NOTHING` plus `SELECT ... FOR UPDATE` and digest comparison. Exact duplicate execution/order event returns `Duplicate` without another row. Same scoped namespace+ID with changed immutable payload returns `Collision` and persists an incident; **never silently increments fills or overwrites**. Events with same ID in another account or venue remain independent.
5. `TryAcquireMetadataLeaseAsync` is an optional monotonic row-generation lease with DB-side expiry checks and 5-minute maximum duration. It **does not authorize** physical sends. Even a correct DB lease cannot revoke already-held external credentials or stale packets at an exchange. B2-02/B3 must own single credential gateway and final fencing prior to network send; never connect this DB lease directly to an adapter's place/cancel methods.

### Crash timeline

```text
Time   DB state                             Safe decision
T0     nothing persisted                    no new command to recover
T1     BEGIN; aggregate+intent+command       not visible; crash rolls back all
T2     outbox NotSent, COMMIT                durable; retry same CommandId returns Duplicate
T3     BEGIN dispatch_started + attempt     crash rolls back both (still NotSent)
T4     COMMIT attempt + outbox NeedsRecovery ambiguous after any later possible send
T5     [future B2-02 gateway may send]       no blind retry after crash/timeout
T6     transport ACK lost                    NeedsRecovery + attempt #1 remain durable
T7     B4 reconciles order/fill histories    only proven facts may resolve unknown outcome
```

**B2 has no T5 implementation.** T4 is a safety boundary, not proof of external send. Crucially B4 must recover even if send did not actually occur. Missing open-order rows alone never mean cancelled/nonexecuted.

## Known limitations / threat model

- **No actual SIGKILL harness in repo**; deterministic pre-COMMIT injected exception, fresh repository after post-COMMIT transition and concurrency tests exercise store guarantees. An external Docker process-kill matrix across T1/T3/T4 is a separate acceptance evidence step. Do not claim process-kill PASS when not run.
- PostgreSQL commit acknowledgement lost is conservatively ambiguous to caller. Caller must query by original scoped command ID (without generating a new ID) to discover durable state. B2 does not yet provide a gateway idempotency handshake.
- Event namespace is adapter-defined and must include FIX session/reset domain as appropriate; choosing a too-narrow/broad namespace is a B6/B7 adapter-qualification problem. No exchange order/cancel/fill projection or B4 reconciliation is wired.
- Request JSON and digest remain immutable under app DB role via triggers, but a database **superuser can bypass triggers**; infrastructure DB roles, audit/WAL retention and backup controls belong to operations. One synthetic risk-decision reference does not mean B5 risk engine has authorized an order.
- `oms_*` typed tables are intentionally **not included in the legacy EF tracked model** (raw SQL migration), avoiding re-mapping legacy snapshot; future EF model migrations must not accidentally drop these manually-owned tables. Schema is versioned separately. Migration is additive; legacy globally unique `ClientOrderId` and `ExchangeFillId` remain unchanged. Backfill and migration of live orders are **out of scope**.
- B1 follow-up (2026-11-03, Issue #58/#67): an authoritative cancel-rejection report with the same positive cumulative fill and revision now projects `CancelPending -> PartiallyFilled`, preserving 3/10 executed and 7 leaves. The B2 cross-contract regression has been updated to verify preservation instead of encoding the old quarantine behavior. Changed fills, untrusted evidence and revision changes remain fail-closed. B2 journal payloads, schema and persistence code are unchanged; the journal still does not consume or reinterpret the B1 reducer.
- DDL `Down` drops only eight B2 tables and B2 guard functions; **refuses rollback when any of the eight new tables contain rows** to prevent accidental data destruction. Archive/export and explicit sign-off required before destructive migration reversal. Existing legacy tables/indexes remain intact.
- No real scheduler/dispatcher, no retry algorithm for `DispatchStarted`, no instrument contract multiplier, no credential custody, no live connection, no multi-leg coordinator. MAINNET read-only invariant is preserved.

## Local-only verification (GitHub Actions forbidden)

Use a **separate disposable PostgreSQL 17** server with no published port or shared data.
Set `TRADEOPS_B2_POSTGRES` only inside the test process; never use a persistent/shared database.
Use the installed .NET10 SDK, or an existing qualified SDK container when appropriate;
do not transplant old WSL settings or pull historical images automatically.

The fixture rejects absent `TRADEOPS_B2_POSTGRES` and non-17 servers before creating
its individually named test databases. The current B2 suite contains **5 pure contract
cases and 23 PostgreSQL cases = 28 executed cases**. The latest runtime evidence is
**28/28 PASS**, independently parsed from TRX; missing/skipped tests cannot qualify the gate.

A build can be made with the repository-pinned SDK. If Ubuntu's installed compatible
runtime SDK differs from global.json, run the portable test assembly through `dotnet vstest`
from a working directory outside that SDK pin, rather than changing global configuration.
The acceptance run records exact source/build, tool versions, selected cases, exit codes,
PostgreSQL version/major validation, raw TRX and resource cleanup.

Intermediate changes run the minimal affected suites. Full unrelated suites and browser
checks are separate gates when the changed execution path requires them; do not repeatedly
run them for the same unchanged code. The current DB-only class has no production DI caller,
so this review does not claim API/Worker/gateway E2E or live exchange qualification.

Migrations and temporary role creation use only the isolated fixture administrator.
Journal acceptance also runs as a generated **NOSUPERUSER/NOCREATEDB/NOCREATEROLE** login,
with OMS table SELECT/INSERT/UPDATE, schema usage and incident-sequence usage. No TRUNCATE,
DDL ownership or production role changes are required. Immutable triggers remain active.
The test role is removed after its connections close; only this test server is affected.

Full evidence and the plan for scoped legacy Order/Fill integration are in the
[final B2 review](../orchestration/workstreams/B2_01_FINAL_REVIEW_2026-11-04.md).
DB-only acceptance is not trading authorization. GitHub Actions remain forbidden.
