# TO-V2-16 — Unified Broker Reconciliation: broker-neutral contracts and pure decisions

**Repository:** PRIVATE `avysotsky/TradeOps-v2`
**Stack base:** TO-V2-15 exact owner-runtime-qualified commit
`d316956d3abfc0fb8c9f59cf4c5991ae99055e52`.
**Safety mode:** design + pure application logic. No runtime DI changes, no broker
sessions, no network or trading, no schema migration, no merge/release.

## Decision

Reconciliation MUST have **one broker-neutral policy/decision layer**
under `TradeOps.Application`. Every venue has its own evidence acquisition
and normalization implementation under `TradeOps.Infrastructure`.

Do **not** build independent end-to-end reconciliation engines for IBKR,
Bybit, Deribit, Binance, etc. Do **not** pretend all venues have the same
API, status semantics, completeness guarantees, security boundaries,
instrument identities or execution-ledger coverage.

```text
TradeOps.Worker / API
           |
           v
Application: broker-neutral reconciler coordinator [FUTURE]
  account/venue scope + lock + recovery run status
           |
           v
Application: IBrokerReconciliationSource (read-only port)
           ^
           |
Infrastructure venue source(s):
  IBKR Paper    Deribit Testnet    Bybit Testnet    Mock ...
    |             |                  |
  provider-specific REST/WS, pages, authentication, rate limits
  and trusted completeness classification
           |
           v
Application: normalized BrokerOrderReconciliationEvidence
           |
           v
Application: BrokerReconciliationDecisionEngine [THIS STAGE]
  fail-closed classification, identity, quantities, execution-ID
  deduplication and transition candidate
           |
           v
Application: atomic reconciliation transaction boundary [FUTURE]
  compare-and-set observation sequence + execution IDs,
  outbox/alerts, order lifecycle and optimistic version checks
           |
           v
Infrastructure: PostgreSQL
```

## Implemented in this PR (deliberately not DI-registered)

- `BrokerOrderReconciliationContext` — immutable instrument identity,
  account, provider, client/broker order IDs, order terms, last accepted
  sequence, local state and cumulative fill. This cross-venue scope is
  currently **missing from the legacy Order entity**; do not
  backfill by guessing the provider from the symbol.
- `BrokerOrderReconciliationEvidence` — explicit
  `Complete`, `NotFound`, `Incomplete`, `Unauthorized`,
  `Unavailable` and `RateLimited` categories. A nullable order
  alone is never a proof of absence. A session or transport failure is
  never converted into `NotFound`.
- `IBrokerReconciliationSource` — read-only provider port without
  place/cancel/retry methods or HTTP assumptions.
- `BrokerReconciliationDecisionEngine` — pure Application service.
  Correlates **provider, account, client ID, any known broker ID,
  immutable instrument ID, side and requested quantity**, requires a
  strictly newer positive observation sequence, and requires complete
  order/execution evidence before emitting an in-memory state candidate.
- Exact cumulative fills must match unique, identity-consistent
  execution IDs; conflicting duplicate IDs, fill regressions,
  impossible totals, unsupported transitions, oversized/malformed IDs
  and unsupported decimal precision fail closed.
- Complete absence always remains `Unresolved`. Unknown/rejected broker
  status and terminal/fill discrepancies do not become commands.
- `BrokerReconciliationDecisionKind.Candidate` is a
  **non-executable proposal** only: never authorization to send a
  broker order or persist a row without the future atomic policy gate.
- `BrokerReconciliationDecisionKind.NoChange` requires aligned local
  state, cumulative fill and already-known broker order ID.
- The decision service is **not added to `TradeOps.Api`,
  `TradeOps.Worker` or DI**, and legacy
  `OrderReconciliationService` remains the live path.

## Known legacy issue that MUST be fixed before migration

Today, legacy `OrderReconciliationService` consumes
`IExchangeClient.GetOrderAsync() -> Order?` and may interpret a
`null` as a missing order. Specifically, the current
`IbkrPaperExchangeClient.GetOrderAsync` maps **both HTTP 404
and HTTP 503** to `null`. A 503 means service unavailable, not
credible complete absence.

**Do not silently route real IBKR through legacy nullable lookup
as a newly trusted order source.** In a later venue-specific
adapter stage, classify 503 as `Unavailable`, HTTP 401 as
`Unauthorized`, HTTP 429 as `RateLimited`, and classify 404
as `NotFound` only when the endpoint's documented semantics and
account/coverage guarantees justify it. A single 404 status endpoint
may NOT prove a complete account order list.

## Important limitations / future phases

1. This stage is **in-memory decision logic**, not an authenticated
   IBKR adapter. The vendor-specific `IbkrOfflineEvidencePageCollector`
   and its synthetic PostgreSQL journal remain separate, accepted
   offline fixtures. No bridge claims these simulated cursors match
   native IBKR pagination.
2. Prior to runtime migration add broker/account/instrument identity
   to persistent orders, an explicit venue-source registry, ownership
   / optimistic concurrency guards, transactional observation digest,
   exactly-once fill-ID persistence, and cross-worker leasing.
3. Versioned snapshot identity and same-sequence replay must be
   reconciled via a **persisted evidence digest**; this pure evaluator
   rejects equal sequence because it cannot prove content equivalence.
4. Separate **order** and **position** reconciliation: common scheduling
   and evidence vocabulary, but distinct aggregation rules, instrument
   netting, currencies, multipliers and tolerance policies. Do not
   naively equate account-wide derivatives exposure to a ticker-only
   crypto position.
5. Adapters must attest pagination coverage and execution ledger
   completeness. Merely assigning `OrdersComplete=true` to a
   synthetic DTO is not a cryptographic or authenticated proof.
6. Future migration must ensure the legacy and new Worker do not both
   update the same account/order concurrently. Add a shadow-mode
   comparison first, then a feature-flagged cutover with rollback.
7. **Never automatically resubmit** an Unknown order even after a
   complete NotFound. Explicit operator-controlled adjudication and
   broker-account qualification would be separate milestones.

## Qualification

The independent WSL2 runner must test the **exact new Draft PR HEAD**:
restore/build/tests PASS, zero skipped, all six cleanup categories true.
Test cases include full fill and partial fill, idempotent execution
replays, conflicting IDs, mismatched provider/account/instrument,
timeout vs missing, incomplete pages and executions, strictly ordered
observations, stale evidence, forbidden transitions and terminal states.

This does not replace owner-executed PostgreSQL 17 end-to-end acceptance
if any runtime service is changed in a future phase. Do not reuse
TO-V2-15's real PG PASS to claim the new pure decision engine has been
runtime-integrated. TO-V2-15 remains the **last independently accepted**
real-database owner runtime until a later integration stage qualifies
the new runtime path.

**No merging of stacked Draft PRs, real orders or NuGet publication.**
