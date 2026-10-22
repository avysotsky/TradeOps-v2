# TO-V2-15 — Synthetic IBKR Evidence Cursor Continuity (Account-Free)

**Repository:** PRIVATE `avysotsky/TradeOps-v2`.
**Base:** Exact TO-V2-14 runtime-accepted SHA
`f0d54abbfa935b4cfc798f9acf523e904a7a6e6a`.
**Branch:** `TradeOps/to-v2-15-offline-evidence-cursor-continuity`.
**Merge/release/order placement:** NOT AUTHORIZED.

## Motivation and trust boundary

TO-V2-14 established real PostgreSQL 17 + synthetic IBKR journal + live
ASP.NET Core API/Worker runtime acceptance. The existing
`IbkrOfflineSnapshotReconciler` properly treats
`OrdersComplete=false` as inconclusive, but callers could previously
fabricate a normalized `OrdersComplete=true` snapshot without any
verifiable page-continuity assembly step.

The new `IbkrOfflineEvidencePageCollector` provides a separate,
explicitly **synthetic-only** local cursor-continuity gate. It never
accesses Interactive Brokers, does not parse the official IBKR Web API
format, does not authenticate the evidence producer, cannot attest that
the actual broker exposed every order, and cannot send a broker request.

**Do not equate successfully assembled synthetic pages with trusted
authenticated IBKR evidence.** A genuine Paper-session fetch plus
transport-specific pagination, account identity, and full execution
coverage must be independently validated before a production adapter
is permitted to consume such evidence. `IbkrPaper` stays read-only.

## Cursor assembly contract

`IbkrOfflineEvidencePage` is a normalized fixture containing a scoped
account, collection identifier, positive observation sequence, 0-based
page number, exact request cursor and next cursor, terminal marker,
and a bounded list of synthetic normalized orders.

`IbkrOfflineEvidencePageCollector` enforces:

- A stable **account / collection / observation sequence** for the
  entire chain, beginning at page index 0 without a request cursor.
- Exact next-request cursor matching and contiguous page indexes;
  no skipped/reordered pages, cycles or reused next cursors.
- No more than 32 pages, 256 total orders, or 4,096 total executions.
- A final page has no continuation token; every nonfinal page MUST
  identify a continuation. No last page means no complete snapshot.
- Byte-value-equivalent normalized page replays are idempotent.
  Conflicting repeated pages or pages following the terminal page
  fail closed with no state change.
- Cross-page order `cOID` and broker order ID uniqueness, valid
  account/side/status/symbol/positive quantity, bounded decimal
  representation, and matching execution identity and quantities.
- A completed execution list must sum to `CumulativeFilled`.
  `ExecutionsComplete=false` remains explicitly untrusted even when
  account-order pagination itself has completed.
- Deep defensive copies into a read-only snapshot; a caller cannot
  mutate the original order or fill lists after they have been accepted.

Two intentionally different results exist:

- `GetPartialSnapshot()` works only before terminal pagination, and
  always carries `OrdersComplete=false`. The reconciler must retain
  `Unknown` and return `Inconclusive`.
- `RequireCompleteSnapshot()` works only after the terminal page
  with an intact cursor chain. It returns normalized full-account
  `OrdersComplete=true` evidence that can be passed into the
  existing *offline-only* reconciler or journal. A valid completed
  **empty** collection is `NotFound`, never authority to resubmit.

A new observation's sequence MUST be increased when evidence
changes from partial to complete; the durable journal deliberately
rejects changing a previously recorded observation at the same
sequence.

## Real database integration

The existing opt-in executable
`tools/TradeOps.IbkrOfflinePostgresSmoke/Program.cs` now feeds the
**real** `IbkrOfflinePostgresJournal.AppendReconciliationAsync`
via two synthetic cursor paths:

1. Interrupted one-page observation, explicitly incomplete -> actual
   PostgreSQL transaction records `Inconclusive`, leaves `Unknown`,
   imports **no execution IDs**.
2. A subsequent separately sequenced complete two-page observation ->
   actual PostgreSQL atomic baseline import and reconciliation to
   `PartiallyFilled`, followed by persisted restart recovery,
   duplicate observation idempotency and fill progression.

These paths are compiled as part of `TradeOps.sln` but **do not
automatically execute real PostgreSQL** on the WSL2 unit-test relay.
The owner must separately execute
`scripts/ibkr/to-v2-14-run-from-windows.ps1` against the **new
exact HEAD**, and verify the real PostgreSQL script's final zero-exit
PASS and cleanup markers. Reusing TO-V2-14 evidence for TO-V2-15
does not qualify this new source.

## Qualification criteria

1. A new Draft PR stacked onto the exact TO-V2-14 accepted branch.
2. Autonomous WSL2 restore/build/tests `overall=PASS`, zero failures,
   complete resource cleanup, exact HEAD pinned.
3. Focused xUnit tests for incomplete and complete chains,
   untrusted execution pages, idempotent replays, cursor
   omission/reuse/cycles/order gaps, account and collection mismatch,
   conflicting execution IDs, numeric limits, defensive copies,
   terminal/empty-page semantics, and bounded page count.
4. Separate genuine PostgreSQL 17 + API/Worker runtime acceptance.
5. No live/Paper order placement, cancellation or unapproved external
   broker request. Keep `broker_requests=0`.

The system deliberately does **not** implement credential handling,
network pagination, IBKR account authorization, UI, automatic retries,
real market-data subscriptions, live order transport, release
publication, migration of any existing database, or mainline merge.
