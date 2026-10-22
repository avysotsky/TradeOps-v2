# TO-V2-12 — Durable offline IBKR reconciliation evidence and fill baseline

**Repo:** PRIVATE `avysotsky/TradeOps-v2`
**Stack:** TO-V2-12 branches from TO-V2-11 Draft PR #13; parent
PRs #12 → #11 → #10 → #9 → #8 are also Draft/unmerged.

## What this changes

TO-V2-09 has a pure in-memory reconciler for ambiguous `Unknown`
orders. TO-V2-10 added a durable synthetic PostgreSQL event and
execution-ID journal. Those milestones lacked a **single atomic**
operation to reconcile a complete broker snapshot, import the
corresponding execution-ID baseline, and record the resulting order
lifecycle event.

TO-V2-12 adds **opt-in offline-only**:

- `scripts/ibkr/to-v2-12-offline-reconciliation-schema.sql`: an
  isolated `ibkr_offline_reconciliation_observations` table, foreign
  keys to existing synthetic intents and lifecycle events, unique
  `(account, cOID, observation_sequence)`, a SHA-256 evidence digest,
  normalized JSON evidence, disposition and optional event sequence.
- `IbkrOfflinePostgresJournal.AppendReconciliationAsync`: a single
  PostgreSQL transaction that:
  1. Locks the matching synthetic intent row using the existing
     `SELECT ... FOR UPDATE` protocol.
  2. Checks monotonic evidence sequence and SHA-256 exact-payload
     replay. Identical latest observation is idempotent; stale or
     conflicting observations fail closed.
  3. Replays persisted lifecycle events into the **existing** pure
     TO-V2-07 reducer; only `Unknown` may be reconciled.
  4. Applies TO-V2-09's offline snapshot/identity/quantity validation.
  5. When complete trusted evidence resolves the order, imports
     **complete** execution-ID baseline rows without recounting
     previously persisted IDs; checks missing/contradictory history,
     decimal scale, and client/broker/account linkage.
  6. Inserts the matching `ReconciledWorking`,
     `ReconciledCancelled` or `ReconciledFilled` lifecycle event,
     plus the evidence observation. The entire transaction commits
     together or rolls back together.
- `IbkrOfflineExecutionBaselinePlanner`: pure, independently unit
  tested, no DB/network; enforces full execution coverage, monotonic
  fill baseline, deduplication, identity consistency and
  numeric(28,12) safety.
- Additional unit tests and expanded
  `TradeOps.IbkrOfflinePostgresSmoke` acceptance CLI.

This is **not** a raw IBKR JSON parser or an authenticated source of
truth. It consumes explicitly trusted *synthetic normalized* snapshots
and produces only local database writes. The existing IBKR provider
remains read-only.

## Safety-critical outcomes

- If pagination/execution evidence is incomplete, preserve `Unknown`
  and store an `Inconclusive` observation without a lifecycle change.
- Complete `NotFound` also preserves `Unknown`; neither outcome
  authorizes a blind retry.
- An accepted `Cancelled` state requires a matching verified terminal
  broker snapshot, not a cancel request receipt.
- A persisted execution with missing/mismatched ID, quantity, account
  or broker order fails closed. New observations cannot erase prior
  fills.
- Same sequence with changed evidence is rejected; a failed database
  transaction must not burn the observation sequence or leave an
  imported fill.
- After an imported partial baseline, the existing journal can append
  **new** synthetic execution IDs and advance to Filled without
  double-counting the historical quantity.

## Qualification

The new PostgreSQL table must be applied **after** the TO-V2-10
synthetic base schema, only inside a disposable, private PostgreSQL
test schema. The opt-in CLI was extended to install both scripts in
that order. It checks partial observation, durable imported fills,
restart replay, observation idempotency/staleness, further fills, and
an SQL uniqueness failure that must roll back both observation and
lifecycle event.

Run only in a provisioned disposable loopback PostgreSQL database
named `tradeops_ibkr_offline_smoke_*` with the explicit opt-in
described in `docs/ibkr/IBKR_OFFLINE_POSTGRES_SMOKE.md`. No passwords,
connection strings or real customer identifiers in code or reports.

The already installed WSL2 runner automatically compiles/tests the
full .NET 10 solution for each new Draft PR SHA and publishes:

`avysotsky/TradeOps-Relay-Queue/autotest-results/tradeops/pr-<PR>/<SHA>.json`

**Build + unit PASS does not establish the PostgreSQL smoke ran.**
Treat actual PostgreSQL runtime acceptance as **NOT RUN** until the
isolated CLI is executed and its success marker is recorded.

## Explicit non-goals

- No real IBKR accounts, orders, confirmation HTTP POSTs, cancellation
  DELETEs, TWS/IB Gateway, remote market data or network adapter calls.
- No runtime DI registration, production DB migration, automatic
  reconciliation worker, real broker-identity trust boundary,
  restart-capable exactly-once broker execution, market-data access,
  external API contract certification or credential processing.
- No GitHub Actions, manual relay tasks, merge, NuGet publication or
  activation of trading mutations without separate owner consent.

## Next qualification stage

An independently audited disposable PostgreSQL acceptance (including
two concurrent workers and crash/recovery scenarios) is still required.
After that, the next workstream can add an offline evidence-cursor
validator without enabling live trading.
