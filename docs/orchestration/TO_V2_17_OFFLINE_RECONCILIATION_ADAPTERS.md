# TO-V2-17 — Broker-neutral reconciliation source adapters (Mock + IBKR offline)

**Repository:** PRIVATE `avysotsky/TradeOps-v2`
**Parent:** Owner-runtime-qualified TO-V2-15 -> unit-qualified TO-V2-16,
exact TO-V2-16 HEAD `59d2b03cae738d1ac62fb9a03932798e1c4ebad0`.
**Draft branch:** `TradeOps/to-v2-17-offline-reconciliation-adapters`.

## Goal / architectural decision

Reuse **one** `BrokerReconciliationDecisionEngine` in
`TradeOps.Application` for all providers. Every venue implements
a separate read-only `IBrokerReconciliationSource` and supplies
normalized, scoped evidence. The engine is the ONLY place
where portable reconciliation transition candidates are evaluated.

This workstream proves adapter compatibility with two *synthetic*
sources, not real broker integrations:

1. **`MockBrokerReconciliationSource`**, backed by a frozen
   `MockBrokerEvidenceSnapshot`. It does NOT call
   `MockExchangeClient.GetOrderAsync`: that implementation advances
   simulated fills as a side effect of reads and has no execution-ID
   ledger. Inferring ledger completeness from that client would be
   false evidence. The fixture is supplied with explicit account,
   sequence, order-list and execution-ledger completeness flags
   and individually identified synthetic executions.
2. **`IbkrOfflineBrokerReconciliationSource`**, backed only by
   `IbkrOfflineEvidencePageCollector` (not a directly supplied raw
   `OrdersComplete=true` DTO). It freezes the collector's current
   partial/complete snapshot and maps synthetic IBKR order/execution
   evidence to `BrokerOrderReconciliationEvidence`.
   Incomplete pagination stays `Incomplete`, and incomplete execution
   lists stay `Incomplete` even after the final orders page.
   A collector that later receives its final page cannot silently
   upgrade a previously instantiated source's frozen observation.

For both adapters:

- Explicit provider/account mismatch is an **error**, never NotFound.
- A known broker-order ID belonging to a different client-order ID
  is an **identity conflict**, never NotFound.
- Only a complete order-list observation with no matching order
  returns `NotFound`. The shared engine keeps it `Unresolved`,
  and neither source has a place/retry/cancel API.
- A matching, complete snapshot with a complete execution ledger
  returns normalized `Complete` evidence. The Application engine
  checks immutable order terms, cumulative fills, replay semantics,
  execution identities, observation sequence and state transitions.
- Deep defensive copies protect the snapshot against caller-side
  array mutation. The IBKR cursor collector already verifies bounded
  pages, cursor continuity and execution identity.
- No database writes, new tables, background tasks, HTTP,
  credential handling, IBKR sessions, real orders or authenticated
  data downloads are included.

## IBKR instrument identity limitation

`IbkrOfflineBrokerReconciliationSource.MakeSyntheticInstrumentKey()`
produces a conspicuous **`SYNTHETIC-IBKR-SYMBOL:`** key, which
preserves only the symbol of synthetic offline fixtures.

It is NOT an IBKR `conid`, contract qualification, exchange,
currency, trading class, multiplier, security type or authenticated
account instrument. A real IBKR Paper adapter must instead use
a fully qualified IBKR contract identifier in a separately approved
implementation. A client context passing raw `AAPL` or a production
conid to the synthetic bridge fails the shared engine's identity
check. No implicit case folding or best-effort symbol matching.

## Explicit shadow evaluation

`BrokerReconciliationShadowEvaluator` in Application:

```text
context
  -> IBrokerReconciliationSource.FetchOrderEvidenceAsync()
  -> BrokerReconciliationDecisionEngine.Evaluate()
  -> BrokerReconciliationShadowResult
```

The result is observational and **non-executable**. The shadow
evaluator does not persist candidate states, does not emit orders,
does not replace the Worker, and is **not registered in DI**. Source
failures and cancellation propagate instead of being converted to
`NotFound` or `NoChange`.

Unit tests run Mock and IBKR offline fixtures through precisely the
same decision engine and check parity for equivalent synthetic
Filled evidence. Negative cases cover incomplete orders and fills,
missing terminal page, unknown broker state, identity collision,
cross-account/provider lookups, wrong instrument namespace,
immutable array copies and source exceptions.

## Existing live reconciliation is intentionally unchanged

- `OrderReconciliationService` / `IExchangeClient` remain the
  existing live Worker path, with no duplicate shadow writes.
- The legacy `IbkrPaperExchangeClient` still conflates HTTP 503
  and 404 in its nullable lookup; this is a documented blocker
  for any future production cutover. This PR neither claims to
  solve it nor expands production IBKR access.
- Future migration requires per-account/provider persistent scope,
  authenticated full-page collection, dedicated 404/503/429 handling,
  durable evidence hashes and transactional execution-ID imports,
  cross-Worker locking and a flag-gated shadow-to-live promotion.

**Do not merge or publish** the stacked Draft PR without reviewing
its parent ordering and proving the new runtime path after cutover.
A past TO-V2-15 real PostgreSQL PASS cannot qualify new runtime
behavior that has not been connected. No new owner PostgreSQL
acceptance is required by THIS workstream because it makes
zero runtime modifications; exact-HEAD WSL2 restore/build/unit
qualification suffices for these isolated adapters.

## Qualification contract

- New Draft PR based on exact TO-V2-16 accepted HEAD
  `59d2b03cae738d1ac62fb9a03932798e1c4ebad0`.
- Independent WSL2 runner: exact SHA, `restore=PASS`,
  `build=PASS`, all xUnit PASS, zero skipped, all six cleanup
  flags true.
- No network call, broker account, real paper/live trade,
  automatic order resubmission or credential consumption.

Following a PASS, preserve that exact HEAD and document the
qualification in the PR discussion. Keep Draft.
