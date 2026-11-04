# B2-01 — final review DB-only OMS journal, 2026-11-04

Issue: [#67](https://github.com/avysotsky/TradeOps-v2/issues/67).
Baseline: `main@ce9721a04f5203923c97a571830f4dba00899d4b`.
Branch: `TradeOps/b2-01-journal-review-fixes`.
Scope: command journal, transactional outbox/inbox, dispatch metadata, schema guards,
and the boundary before runtime integration. No exchange connection or sending.

## Review verdict

**PASS for the DB-only B2 scope:** 23 PostgreSQL integration cases and 5 pure contract
cases actually executed, 28/28 passed, 0 failed/skipped; raw TRX independently checked.
The decimal replay defect is fixed and the restricted-role scenario passed.
This completes the journal review in Issue #67. It does **not** qualify an executable
gateway, physical fencing, live adapter, legacy backfill or production order entry.

## Confirmed defect and correction

An equal decimal can have a different scale: `1m == 1.000m`, but the serializer can
produce different JSON bytes and SHA-256. The previous implementation compared only
the digest and quarantined a valid repeat as `Collision`.

Three new PostgreSQL regressions reproduced the defect: inbox quantity/price,
Limit command quantity/price, and Stop command quantity/trigger. The red invocation
also selected one existing unrelated scale test: **3 FAIL, 1 PASS**, not four new cases.

The existing schema-v1 JSON representation and first-writer hashes remain unchanged.
Under the existing scoped row locks, matching uses the digest or equality of the
**complete versioned JSONB document**. This is equality, not JSON containment or a
quantity-only comparison. Scope, IDs, expected version, parameters and provenance
still participate; a genuinely changed quantity/price remains `Collision`.

The command and inbox regressions check that a duplicate does not rewrite original
JSON/hash, increment aggregate version, create outbox rows or append a false incident.
No migration, data rewrite or schema-version change is required, including for
existing v1 rows. PostgreSQL documents numeric scale as insignificant for JSONB
equality: [PostgreSQL 17 JSON types](https://www.postgresql.org/docs/17/datatype-json.html).

## Code review matrix

| Boundary | Reviewed implementation | Finding |
|---|---|---|
| Full scope | `ScopeWhere`, parameters and composite keys include tenant, venue, environment, account, subaccount | No unscoped journal lookup found; missing subaccount maps to the explicit empty partition |
| Durable command | One ReadCommitted transaction: scoped arbitration, intent ownership, aggregate CAS, command and outbox | Failure/stale CAS rolls back tentative rows; command collision is checked before aggregate seeding |
| Dispatch boundary | Command `DispatchStarted`, attempt #1 and outbox `NeedsRecovery` commit together | Restart cannot create another attempt through this API; no send occurs in this class |
| Outcome | Command/attempt/outbox updated together; ambiguity stays recoverable | Acknowledged is transport disposition, not fill; no reset to `PersistedNotSent` |
| Inbox | Immutable scoped namespace/event ID, payload comparison and incident append | Store-only; it does not itself project or double-count fills |
| Concurrency | Transaction-scoped Command/Intent locks, unique keys, aggregate version CAS, row locks | DB lease/advisory locks have no physical-send authority |
| Schema | Additive eight-table migration and mutation guards | Nonempty Down refuses destruction; legacy schema unchanged |
| Runtime boundary | No other production caller/DI registration of `OmsPostgresJournal` found | No claim that the journal is the current authoritative application execution path |

`Duplicate` is not proof that a command was never sent. Future callers must inspect
the durable dispatch/recovery state and honor the atomic send boundary. Fault tests
inject pre-commit exceptions and reconstruct repository instances; they are not a
real SIGKILL or lost-COMMIT-acknowledgement harness.

## Restricted application role

A dedicated acceptance case creates a generated temporary login in the disposable
test server, explicitly without superuser, role/database creation or replication.
It grants schema usage, SELECT/INSERT/UPDATE on the OMS tables and usage of the incident
identity sequence. UPDATE permission is needed for row locking and guarded state changes;
append-only triggers continue to reject mutation of immutable evidence.

The case exercises command persistence, decimal replay and real collision, dispatch,
ambiguous outcome, restart/no resend, acknowledgement, inbox replay/collision and
metadata lease acquisition. It verifies that direct payload mutation is rejected and
TRUNCATE is not permitted. Migration/role setup uses only the isolated fixture admin;
the journal operations use the restricted login. No persistent application role changes.

This proves the tested store operations do not require a superuser. It does not qualify
deployment grants, RLS, arbitrary DBA tampering protection or current production credentials.

## Legacy Order/Fill integration plan

1. Introduce application ports for journal/inbox/recovery queries and bind the PostgreSQL
   implementation in composition with explicit account context. Keep transport and
   credential ownership outside domain contracts. A risk-reference string alone does
   not prove the B5 risk gate was executed.
2. Add a separately reviewed mapping from legacy `Order.Id`/`Fill.OrderId` to the complete
   B1 `AccountKey` and OMS aggregate. Require authenticated provenance for broker IDs,
   instrument/native units and account context. Do not infer scope from Symbol, a bare
   exchange ID, the current UI account, or a default tenant.
3. Existing `BrokerOrderIdentities` is explicitly `SyntheticOnly`, with provider/instrument
   checks and no full tenant/environment scope. Do not upgrade its rows to verified live
   identity or use it as the production backfill source. Unknown legacy scope stays
   quarantined/read-only until reconciled.
4. Keep legacy global ClientOrderId/ExchangeFillId constraints until a separate additive
   backfill and scoped uniqueness plan are qualified on disposable data. Preserve all
   fills and original identifiers. Do not deduplicate cross-account fills by a bare ID.
5. B3 first captures Deribit private events read-only into the scoped inbox, using a durable
   execution-ID namespace and an explicit stream/recovery epoch. Buffer/gap/reconnect,
   snapshot and fills-history coherence are B3/B4 gates; open-order absence is never a
   cancellation proof. Matching an initially uncorrelated inbox event belongs to a separate
   projection/link, not mutation of its original immutable capture.
6. B4 connects journal/inbox to authoritative order/fill projections and recovery reads,
   preserving ambiguous outcomes and cancel/replace races. B5 gates risk and kill switch.
   Before any sending, qualify a single credential-owning gateway with physical fencing;
   neither a DB metadata lease nor these tests provide it.
7. Deribit FIX 4.4 through QuickFIX/n remains mandatory in V2 after these prerequisites.
   Venue-mutating testnet qualification still needs separate owner authorization.

## Executed evidence

- Regression red: 3 new FAIL, 1 existing PASS; expected `Duplicate`, actual `Collision`.
- Test-project/dependency build: PASS, 0 errors, 15 pre-existing xUnit analyzer warnings.
- Selected B2 PostgreSQL and pure contract suites: **PASS 28/28**, 0 failed/skipped;
  23 PostgreSQL cases (19 existing + 4 new case executions), 5 pure contract cases.
  The two Limit/Stop theory rows count as separate executed cases.
- Restricted application role: **PASS**, including payload-mutation and TRUNCATE denials.
- Resource Guard: **PASS** before/after red and green; temporary resources removed.
- Existing-container snapshots: **PASS**, same IDs/start times/restart counts/status/health.
  Owner's uncommitted launch settings: **PASS**, hash unchanged.
- Full unrelated backend suite, browser E2E and live exchange qualification: NOT_RUN.

Tests use the existing Windows .NET SDK 10.0.401 for build and Ubuntu SDK 10.0.114
for portable execution; no SDK/WSL changes. Temporary PostgreSQL 17.11 is limited to
384 MiB and 0.5 CPU, with an internal unique network, no published ports or permanent
volumes; tests run sequentially on one CPU. Cleanup verifies resource ownership labels.
The persistent database and other projects are not used by this acceptance.

Local logs/TRX and before/after container snapshots are in the ignored
`.tradeops/b2-review-20261104/` directory. No secrets or raw private events are published.
