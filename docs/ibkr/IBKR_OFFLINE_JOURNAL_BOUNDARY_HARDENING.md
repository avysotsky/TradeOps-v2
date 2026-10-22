# TO-V2-13 — IBKR offline PostgreSQL journal security boundary

**Stacked on** TO-V2-12 Draft PR #14; preceding PRs #8–#13 remain
Draft/unmerged. Scope is still **100% synthetic and account-free**.

## Independent audit findings and corrections

### 1. Prevent accidental use against arbitrary PostgreSQL databases

Prior to TO-V2-13, `IbkrOfflinePostgresJournal` accepted an arbitrary
connection string, even though the docs recommended disposable local
databases. This is too permissive for an intentionally offline,
unintegrated experimental order journal.

`IbkrOfflineJournalConnectionPolicy.Validate` now runs in the journal
constructor, **before any connection or query**. It accepts only:
- `localhost`, `127.0.0.1` or `::1` as the exact single PostgreSQL host.
- Database name `tradeops_ibkr_offline_smoke_*` (nonempty suffix, lowercase
  ASCII/digits/underscore, at most 63 characters).
- Exact `Search Path=ibkr_smoke_<32 lowercase hex characters>` (no
  secondary schema, `public`, `pg_temp`, commas or whitespace).
- No connection `Options` overrides that could modify session settings.

All other targets fail closed with a generic exception that does not
include the connection string or credentials. The opt-in CLI now checks
unsafe root-level Search Path/Options *before* creating a schema and
validates the final scoped string before applying DDL.

**This is defense in depth, not sufficient security by itself.** A
local TCP forwarding service might still point to an unintended
PostgreSQL cluster. The test role must have restricted privileges and
be provisioned against a truly disposable database. No production
credentials or real account data are permitted.

### 2. Make evidence-bound state transitions impossible via direct append

`IbkrOfflinePostgresJournal.AppendEventAsync` previously forbade direct
`CumulativeFill` but allowed direct `ReconciledWorking`,
`ReconciledFilled`, `ReconciledCancelled`, `CancelConfirmed` and
`ReconciliationNotFound`. This could create an apparent successful
reconciliation or final cancellation **without** the accompanying
authenticated-by-design *synthetic* evidence and execution-ID baseline.
It also bypassed the TO-V2-12 atomic import invariant.

`IbkrOfflineJournalReplay.ValidateDirectAppend` now blocks all of
these event types **before opening PostgreSQL**. Required paths are:
- `AppendExecutionAsync` for a new fill, which persists the execution
  ID and cumulative state atomically.
- `AppendReconciliationAsync` for resolving `Unknown`, which validates
  complete normalized evidence, imports the execution baseline and
  persists the accepted observation plus lifecycle event in the same
  transaction.
- Inconclusive/NotFound observations stay in `Unknown` and are kept in
  the observation ledger, not forged via direct lifecycle events.

Tests include direct-append bypass regressions, remote DB and
multi-host strings, public/mixed search paths, options overrides,
non-disposable DB names, connection error redaction and valid local
smoke schema configurations. The opt-in PostgreSQL smoke also attempts
forged terminal and NotFound events, asserting the journal remains
`Unknown`.

## Execution/qualification

The new Draft PR is automatically subject to independent WSL2
restore/build/unit-test and cleanup qualification at its **exact SHA**:

`avysotsky/TradeOps-Relay-Queue/autotest-results/tradeops/pr-<PR>/<SHA>.json`

A PASS **does not** prove that the opt-in PostgreSQL smoke executable
ran. That is a separate runtime gate, still **NOT RUN** until a trusted
disposable PostgreSQL environment executes the smoke and records its
exit status/marker.

Existing `IbkrPaperExchangeClient` stays read-only. No live or Paper
order placement, authenticated broker connectivity, network feeds,
production DB activation, migrations, GitHub Actions, merge, release
or NuGet package publication.
