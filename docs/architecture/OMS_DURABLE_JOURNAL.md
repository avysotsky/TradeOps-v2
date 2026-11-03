# B2-01 — Durable OMS journal, PostgreSQL 17 (store-only v1)

**Status (2026-11-03):** DB-only implementation integrated into main; 19 PostgreSQL17 scenarios passed on the integrated tree. Final B2 review remains in [Issue #67](https://github.com/avysotsky/TradeOps-v2/issues/67). **NOT an order-sending gateway**. Historical source branch `TradeOps/b2-01-durable-command-journal`; base `TradeOps/b1-01-oms-domain-identity-contracts@a799aec77ef498c88977a3ee6466989c1a0958ee`. Roadmap PR #56, parent B1 PR #62. See the [integration evidence](../orchestration/DERIBIT_V2_SECURITY_INTEGRATION_2026-11-03.md) and [B1 follow-up](../orchestration/BACKLOG_CLEANUP_AND_B1_FIX_2026-11-03.md) for exact scope and limits.

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

The schema uses `jsonb`, server-side `clock_timestamp()` timestamps (`timestamptz`), simple `bytea` digests, PostgreSQL checks, composite FKs and atomic unique constraints. `OmsJournalCanonical` serializes a fixed-version, fixed-field request (including full scope, IDs and expected version) with deterministic System.Text.Json options and hashes the UTF-8 bytes. Maximum payload 16 KiB. Validation rejects whitespace-only/oversized inputs, invalid enum and GUIDs, non-UTC request event timestamps, nonpositive quantities/prices. The strongly typed payload carries instrument, side, order type, units, quantity, **limit, trigger, time-in-force, reduce-only, post-only, provenance, operator/strategy, risk decision reference and request UTC time**, plus mandatory target client/exchange order reference for Cancel/Replace and optional distinct FIX ClOrdID/OrigClOrdID correlation. Nulls are explicit. No API keys, auth headers or transport serialization strings are accepted. An existing command ID with differing payload hash is `Collision`; no upsert mutates the original.

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

Use a **separate disposable PostgreSQL 17** Docker network/container with no published port and a .NET SDK 10 container under WSL2. The exact B2 branch HEAD must be checked against GitHub `ls-remote` and cloned/exported into a disposable tree; ensure the `TradeOps/b1-01-oms-domain-identity-contracts` base SHA matches the assigned parent. Set `TRADEOPS_B2_POSTGRES` to the isolated PostgreSQL17 admin connection for integration tests; no production DB. Tests create/drop individually named temporary databases, so never point them at a persistent/shared PostgreSQL service.

```bash
dotnet restore TradeOps.sln
dotnet build TradeOps.sln -c Release --no-restore
TRADEOPS_B2_POSTGRES='Host=postgres;Port=5432;Database=postgres;Username=postgres;Password=<synthetic>' \
  dotnet test tests/TradeOps.UnitTests/TradeOps.UnitTests.csproj -c Release --no-build \
  --filter 'FullyQualifiedName~OmsJournal' \
  --logger 'trx;LogFileName=B2-01.trx'
dotnet test TradeOps.sln -c Release --no-build
```

**Fail-closed requirement:** each `OmsJournalPostgresTests` case throws `POSTGRES_ACCEPTANCE_NOT_RUN` if `TRADEOPS_B2_POSTGRES` is absent. The fixture also queries `server_version_num` and rejects anything except PostgreSQL major version **17** before creating a disposable test database. Written regression suite: **5 isolated B2 contract tests plus 19 PostgreSQL integration tests = 24 tests**, including concurrent same CommandId with different payload and concurrent same IntentId against different aggregates, dispatch-precommit rollback, migration Up/Down and non-rearmable outbox. A successful `dotnet test` exit by itself is not enough: the local runner must verify the B2 TRX has at least 24 actual passing, zero skipped tests. These tests are **NOT RUN** until executed in WSL2/Docker and evidenced by log files.

Acceptance **requires** full logs, TRX and `summary.json` recording: exact HEAD SHA, build exit code, total/passed/failed/skipped tests, PostgreSQL17 version, execution environment, failpoints and PASSED/FAILED/NOT_RUN. A test suite run without `TRADEOPS_B2_POSTGRES` is **not** a PostgreSQL acceptance PASS, even if xUnit reports green. Hosted GitHub Actions must never be started. B1/G0/orchestrator acceptance and B2 stack review are separate gates.
