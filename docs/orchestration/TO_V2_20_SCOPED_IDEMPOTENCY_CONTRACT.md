# TO-V2-20 — Account-Scoped Idempotency Contract (Synthetic, Read-Only)

**Repository:** PRIVATE `avysotsky/TradeOps-v2`
**Parent:** TO-V2-19 owner-real-PostgreSQL-17-runtime-qualified HEAD
`47b048e00f99fc7a328dd08450aa14675cc221a9`
**Workstream:** `TradeOps/to-v2-20-scoped-idempotency-contract`
**Status:** Draft, non-runtime Application policy only.
**Safety:** no broker sessions, real order placement, migrations, production writes,
index changes, DI registrations, live Worker changes, publication or merge.

## Why an additional stage is required

The TO-V2-19 additive `BrokerOrderIdentities` sidecar creates scoped
identities without modifying the accepted legacy `Orders` and `Fills`.
However legacy persistence and API still use global IDs:

| Existing path | Current assumption | Multi-provider migration risk |
| --- | --- | --- |
| `OrderConfiguration` | Unique `Orders.ClientOrderId` globally | Two accounts cannot currently reuse an identical client ID even when their accounts differ |
| `EfOrderRepository.GetByClientOrderIdAsync` | Single unscoped cOID | Could return another account's order after unique index relaxation |
| `EfOrderRepository.GetByExchangeOrderIdAsync` | First matching broker ID | Broker order IDs are not globally unique or globally bound to an account |
| `EfOrderRepository.TryAddAsync` | Any PostgreSQL unique violation treated as duplicate | Scoped schema needs conflict-class and constraint-specific handling |
| `EfFillRepository` / `IFillRepository` | Global `Fills.ExchangeFillId` unique | Execution IDs may collide across accounts or venues |
| `OrdersController` operator routes | Path IDs and cOID lack explicit account and broker | Cancels/reads must not become cross-account ambiguous |
| `OrderReconciliationService` | Legacy IExchangeClient nullable order lookup | A transport failure is not reliable absent-order evidence |
| `TradingSignal` ingress and replay receipts | Existing source/signal idempotency contracts | Inserting scope into only one layer would break exactly-once signal semantics |

These are architectural blockers to *removing* global uniqueness.
**Do not drop or relax any of the old constraints** until every writer,
reader, ingestion, signal deduplication, cancellation and recovery path
has a tested account-aware alternative. In particular, never use ticker
text alone to infer provider/account/contract.

## Implemented in TO-V2-20

One broker-neutral, pure `TradeOps.Application` policy:

`BrokerScopedIdempotencyPolicy.CompareOrders(existing, incoming)`

`BrokerScopedIdempotencyPolicy.CompareExecutions(existing, incoming)`

It compares synthetic identities including:

- `ProviderKey` and `AccountKey` (case-sensitive, never normalized by
  best guess)
- stable local `OrderId` (a GUID, never transferable across scopes)
- scoped `ClientOrderId`, nullable `BrokerOrderId` and qualified
  synthetic `InstrumentKey`
- order `Side`, `OrderType`, `RequestedQuantity` and optional `Price`
- execution `ExecutionId` bound to exactly one local order, plus
  `Quantity`, `Price`, `Fee` and `FeeCurrency`

The policy explicitly distinguishes:

| Disposition | Meaning | What it **does not** permit |
| --- | --- | --- |
| `DistinctScopedIdentity` | Different normalized account/provider or non-overlapping identifiers | No permission to insert, send an order, or bypass a database-wide uniqueness check |
| `EquivalentReplay` | Same scoped key and immutable payload | No bypass of persisted execution-ID ledger or same-sequence evidence digest |
| `IdentityConflict` | cOID, broker ID, local GUID, execution ID or immutable payload contradictions | No overwrite, switch of broker account, or blind replay |
| `BrokerBindingRequiresReview` | Same order previously had no broker order ID and now carries one | No automatic binding without authenticated broker-source and durable history proof |

Every input is checked before comparison. Only **Mock** and
**IbkrOffline** synthetic providers are accepted in this stage.
`IbkrPaper` and live venues fail closed: this policy does **not**
provide production IBKR account, contract (conid/exchange/currency)
or execution proof.

All amounts are strict finite `decimal` values with at most 12
fractional digits, within the legacy EF Core `decimal(28,12)`
supported precision envelope. The policy rejects unknown enum
values, incomplete or malformed identifiers and account keys.

## Collision cases explicitly tested

- Same cOID and same execution ID are **distinct** in separate accounts.
- Same cOID and same execution ID are **distinct** across providers.
- Local `OrderId` reused under different account/provider is a conflict.
- Same cOID assigned to a different local `OrderId` in one account is a conflict.
- Same broker order ID assigned to a different cOID in one account is a conflict.
- Duplicate execution ID assigned to a different local order in one account is a conflict.
- Identical execution ID with changed quantity, fee, currency or price is a conflict.
- Broker-order-ID first binding is review-only; changing or removing a
  previously known broker ID is a conflict.
- Same scope and complete identical immutable order/fill evidence yield
  `EquivalentReplay`, not a state mutation or resubmission.
- Incomplete/invalid provider, synthetic contract or quantity always fails closed.

## What this intentionally does NOT do

This is not an in-memory deduplication cache, an exchange adapter, a
runtime writer, or a transactional SQL idempotency reservation. It has
no DI registration, database dependency, network access or background
worker. A `DistinctScopedIdentity` result is **only a pairwise
comparison**, not proof the key is absent elsewhere in PostgreSQL.
The existing TO-V2-19 scoped sidecar remains explicitly synthetic only.

### Required subsequent implementation steps (separate review + real DB gates)

1. **Read migration:** require authenticated provider/account/contract
   scope at every order/exec lookup; retain legacy paths only for
   explicitly unscoped historical rows that cannot be used for active
   venue operations. Separate query identities from operator audit.
2. **Write migration:** establish durable transaction-level scoped
   order and execution reservations using unique constraints
   (`provider,account,clientOrderId`) and
   (`provider,account,executionId`) with checked payload and order
   ownership. Preserve local GUID and use conflict-specific SQLSTATE/
   constraint names. All local order/fill commits require atomic
   reservations and persisted accepted-observation digests.
3. **Signal ingress migration:** move API replay receipt and
   idempotency to explicit scope; no cross-account fallback.
   Set strict 409/conflict policy for same scoped ID with changed
   immutable order fields.
4. **Reconciliation migration:** authenticated venue source, valid
   instrument identity, complete execution coverage, observation
   sequence and concurrency guard. `Unknown` orders must never be
   automatically resubmitted even if the broker returns NotFound.
5. **Old index removal:** only after all live readers/writers and
   migration gates are verified may the global `Orders.ClientOrderId`
   and `Fills.ExchangeFillId` uniqueness constraints be reconsidered.
   Existing historical rows may remain quarantined/unscoped; no
   inferred broker backfill.
6. **Runtime gate:** new exact-HEAD WSL2 restore/build/unit plus
   disposable REAL PostgreSQL 17 + API/Worker acceptance for any
   actual runtime migration. Include two synthetic accounts with
   identical external IDs and disambiguated HTTP/SQL reads, plus
   concurrency and rollback conditions.

## Qualification required NOW

Only the independent exact-SHA WSL2 restore/build/xUnit/cleanup
suite is needed for this pure, DI-unregistered Application
contract. **Do not** require another expensive owner PostgreSQL
acceptance when no runtime behavior or schema changed. Preserve
the already accepted TO-V2-19 SHA.

**Keep Draft. No merge, order placement, account access or publication.**
