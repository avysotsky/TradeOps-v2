# TO-V2-10 — Durable IBKR offline execution journal (opt-in PostgreSQL)

**Repository:** PRIVATE `avysotsky/TradeOps-v2`
**Stacked on:** TO-V2-09 Draft PR #11 (and #10/#9/#8)
**Safety:** No real IBKR or Paper account; no broker HTTP, socket client,
credentials, live orders, order confirmation or cancellation.

## Purpose

TO-V2-07 through TO-V2-09 use in-memory order lifecycle and
reconciliation fixtures. Restarting a process would lose those identities.
TO-V2-10 introduces an isolated, opt-in PostgreSQL **synthetic order
journal** preserving:

1. Immutable intent by composite `account_key + client_order_id`.
2. Append-only ordered lifecycle event trace with unique `event_key`.
3. Immutable execution ID, correlated broker order ID, quantity and
   account/order identity. Execution ID is unique in an account.
4. Deterministic replay to reconstruct the existing offline lifecycle
   reducer after process restart.

This is an **engineering rehearsal**, not a production execution ledger.

## Files

- `src/TradeOps.Infrastructure/Exchange/Ibkr/IbkrOfflineJournalReplay.cs`
  enforces canonical event keys, payload fields, quantity precision,
  mandatory scope and replay integrity.
- `src/TradeOps.Infrastructure/Exchange/Ibkr/IbkrOfflinePostgresJournal.cs`
  implements explicit `RegisterAsync`, `AppendEventAsync`,
  `AppendExecutionAsync`, `LoadAsync`. All calls require a separately
  constructed connection string. It is **not** configured/registered in
  the existing API or Worker service container.
- `scripts/ibkr/to-v2-10-offline-journal-schema.sql` contains isolated,
  opt-in tables. The existing `Orders`, `Fills`, existing EF model
  snapshot and application migrations are unchanged.
- `tests/TradeOps.UnitTests/IbkrOfflineJournalReplayTests.cs` exercises
  purely local durable-format replay rules without requiring a database.

## Transaction and concurrency guarantees

The PostgreSQL journal performs each logical operation in a transaction
and first selects the intent row `FOR UPDATE`. This serializes writers
for the same account and client order **across separate connections**.
A conflicting intent is refused (no replacement), and event keys are
uniquely constrained. Order event sequences are gap-free and append-only.
Each fill inserts an execution row and a cumulative fill lifecycle event
in the **same PostgreSQL transaction**. Identical execution IDs do not
increase the cumulative quantity twice; a collision with a different
quantity or broker order ID is rejected.

The journal records `SubmissionStarted` before a hypothetical external
action. After a simulated process crash and restart, replay reconstructs
`AwaitingAcknowledgement`; the reducer refuses another start. Likewise,
a persisted `Unknown` state remains uncertain after a restart.

A recovery row contains the restored snapshot and immutable event/fill
history. The journal never marks a request completed just because its
cancellation was acknowledged; cancel/fill races follow TO-V2-07 rules.

### Conservative design boundaries

- Direct `CumulativeFill` appends are forbidden; use
  `AppendExecutionAsync` to atomically persist an execution ID and event.
- If reconciliation established a nonzero cumulative quantity without
  persisted execution IDs, later fills are **blocked** rather than
  incorrectly counted. Future work must implement a controlled
  backfill/import of the exact broker execution ledger.
- The journal has no real broker data source. It cannot prove complete
  IBKR histories or canonical IBKR `execId` semantics.
- No automated retry, external order submission, cancellation,
  preflight transport, execution parser, DI registration, migration
  application or persistent production recovery worker exists.
- At the current stage this is **not** a formally verified atomic
  exchange-execution workflow, even though local DB writes are atomic.
- A real PostgreSQL DB runtime acceptance has to be separately confirmed:
  build/unit-test PASS proves compilation and replay correctness but not
  PostgreSQL transaction behavior.

## Explicit disposable-DB setup (not part of application deployment)

Only a dedicated, isolated test database with disposable credentials
should use the schema script. It is intentionally **not** run by
`TradeOps.Api`, `TradeOps.Worker`, startup, migrations, or GitHub
Actions. Do not apply it to a real trading database.

One example for a trusted offline lab already provisioned by its
administrator:

```bash
psql "$IBKR_OFFLINE_TEST_PG" -v ON_ERROR_STOP=1 \
    -f scripts/ibkr/to-v2-10-offline-journal-schema.sql
```

The connection string is supplied only in the isolated lab; this is
an opt-in operator instruction **not** a request for the owner to shuttle
test tasks. No password should be printed or committed.

## Offline tests

```bash
dotnet test tests/TradeOps.UnitTests/TradeOps.UnitTests.csproj \
    --filter FullyQualifiedName~IbkrOfflineJournalReplayTests
```

The autonomous WSL2 runner tests each new Draft PR HEAD and writes
`avysotsky/TradeOps-Relay-Queue/autotest-results/tradeops/pr-<PR>/<SHA>.json`.

Treat a file as authoritative only when its SHA equals the current PR HEAD,
restore/build/test all PASS, test count is real and cleanup all PASS. No
manual relay queues, GitHub Actions, merge or NuGet publishing.

## Next development milestones

1. Isolated PostgreSQL end-to-end transaction/restart tests with a
   disposable schema and no external broker access.
2. Durable reconciliation evidence observations and execution-ledger
   baseline import with verified cursor/completeness semantics.
3. Account-scoped concurrency and crash-injection testing; review
   secret management and multi-tenant boundaries before use in Paper.
4. Authenticated Paper-only qualification under separate explicit
   authorization. Live trading remains disabled.
