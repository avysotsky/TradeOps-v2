# B1-01 — OMS identity and lifecycle contract (v1)

Status: **additive domain contract v1 (`OmsContractSchema.Version = 1`), not a production OMS migration**. Issue [#58](https://github.com/avysotsky/TradeOps-v2/issues/58), roadmap [PR #56](https://github.com/avysotsky/TradeOps-v2/pull/56). Source baseline: `main@c7a0d29028d7cf4958a26aa4879b99b3e39f7b14`.

## Scope and invariants

- `AccountKey = (TenantId, VenueId, Environment, TradingAccountId, optional TradingSubaccountId)`. Every order, label, exchange order, execution ID, command, audit item and replay cursor must carry the scope. Venue is a canonical registry key (case-normalized); opaque account/subaccount and exchange identifiers are compared ordinally, **case-sensitively**. Do not trim or reinterpret exchange IDs.
- `OmsLocalOrderId` is internal OMS aggregate identity, never a broker identifier. `OrderIntentId` identifies immutable strategy/operator intent; `OmsCommandId` identifies an operation under that intent; `DispatchAttempt` is a monotonic attempt number; `OrderVersion` is local projection version; `OrderRevision` describes accepted/attempted cancel-replace revision and is distinct from version.
- `ClientOrderLabel`, `ExchangeOrderKey`, `ExecutionKey` are mutually distinct opaque types. `ScopedClientOrderId`, `ScopedExchangeOrderId` and `ScopedExecutionId` add the full `AccountKey`; execution namespace is adapter-defined, and for FIX can include session/execution ID domain. Never reconcile an order by bare label or exchange order ID in a global multi-account store.
- `FixClOrdId` (tag 11), `FixOrigClOrdId` (tag 41), FIX ExecID (tag 17, represented as adapter-namespaced `ExecutionKey`) and exchange OrderID (tag 37) must remain separate. Replace/cancel request IDs, original request IDs, acknowledgements and execution report IDs have venue-specific mapping. Deribit label is **not** guaranteed globally unique, nor interchangeable with FIX ClOrdID. Adapter verifies string length, allowed characters, uniqueness and field-specific scope; domain intentionally does not encode Deribit/Binance/CoinCall limits.
- First verified remote order ID/label may bind to an existing local aggregate; a later conflicting binding is quarantined as `RequiresReconciliation` and cannot overwrite an existing correlation. No application-level registry/DB constraint is added in B1.
- Exchange-originated orders may lack an `OrderIntentId` or client label. They require an exchange ID and a fresh locally assigned aggregate ID. Do not fabricate local placement/acknowledgement.
- Original `OmsOrderTerms` lock instrument, side, type, decimal requested quantity and explicit unit (`Contracts`/`BaseAsset`/`QuoteAsset`/`Shares`/`Lots`). Contract multiplier, settlement currency, inverse contract amount and instrument version must be supplied by later B1 instrument rules/B7 venue qualification; unit conversion is **not** implicit. Replacing quantity/price means a separately recorded order revision and a new validated terms snapshot in B2; current B1 reducer keeps original total fixed.

## State machine

OMS phases: `Created`, `DispatchPending`, `Submitted`, `Accepted`, `PartiallyFilled`, `CancelPending`, `ReplacePending`, `Unknown`, `Filled`, `Cancelled`, `Rejected`, `Expired`.

| Current state | Allowed next states (other than same-state replay) |
|---|---|
| Created | DispatchPending, Submitted, Accepted, PartiallyFilled, Filled, Cancelled, Rejected, Unknown |
| DispatchPending | Submitted, Accepted, PartiallyFilled, Filled, Rejected, Cancelled, Unknown |
| Submitted | Accepted, PartiallyFilled, Filled, Rejected, Cancelled, CancelPending, ReplacePending, Unknown |
| Accepted | PartiallyFilled, Filled, Cancelled, Expired, CancelPending, ReplacePending, Unknown |
| PartiallyFilled | Filled, Cancelled, Expired, CancelPending, ReplacePending, Unknown |
| CancelPending | PartiallyFilled, Filled, Cancelled, Accepted (cancel rejected), Unknown |
| ReplacePending | Accepted, PartiallyFilled, Filled, Cancelled, CancelPending, Unknown |
| Unknown | Submitted, Accepted, PartiallyFilled, Filled, Cancelled, Rejected, Expired **only with authoritative remote evidence** |
| Filled, Cancelled, Rejected, Expired | No status rewrite; cancelled may receive *late confirmed fills* while remaining cancelled |

`Created -> exchange outcome` allows *confirmed* exchange-discovered orders; local command evidence cannot manufacture a filled, rejected or cancelled order. Terminal = Filled/Cancelled/Rejected/Expired. DispatchPending, Submitted, Unknown, CancelPending and ReplacePending have pending/uncertain outcomes; **all nonterminal orders**, including Accepted and PartiallyFilled, must be accounted for in pending exposure and risk budget. Terminal status is never evidence of account reconciliation completion.

`OmsLifecycleContract.Apply(current, observation)` returns `Applied`, `Duplicate`, `Stale`, or `RequiresReconciliation` and an immutable next/current state. No IO, no clock, no accidental persistence.

- Cumulative fill is monotone in the original unit, 0..requested. `PartiallyFilled` requires strictly (0, requested); `Filled` requires exactly requested. Nonterminal leaves = requested minus cumulative; terminal leaves = 0 **even when cancelled after partial fill**. Inconsistent exchange leaves are a reconciliation issue, not a silently corrected projection.
- Source labels: `LocalCommand`, `PrivateOrderReport`, `OrderLookup`, `OrderHistory`, `ExecutionFill`, `OpenOrdersPresence`. A local command never proves a venue outcome **or increase cumulative executed quantity**. Open-order presence cannot prove terminal state. Missing from open-order listing is returned explicitly as `RequiresReconciliation`, without status change.
- A local `Unknown` after possible transmission prohibits automatic resend. `OmsDispatchSafety.IsProvablyNotSent` is true **only** for Prepared / PersistedNotSent; the latter is a B2 journaling guarantee, not a blind deduction after process crash. `DispatchStarted`, `Acknowledged`, `Rejected`, `Ambiguous` are ineligible for automatic reissue. Unknown can be cleared only with authoritative order lookup/history/private execution evidence and gap checks (B4).
- Compare per-stream ordered cursors only within identical `(stream, epoch)`. Lower sequence = stale; repeated sequence+identical state = replay; repeated sequence+conflicting state = reconcile; incomparable streams/epochs = reconcile until a B4 epoch reset. Event timestamps **are not** causal sequence numbers. Missing venue-native sequence means monotonic fills/status invariants still apply, but ordering proof is incomplete.
- Late confirmed execution on `Cancelled` may increase cumulative fill without reopening the cancelled order. **A fill event cannot establish Cancelled unless the order was already confirmed Cancelled by an order report/history.** Conflicting `Filled -> Cancelled`, `Cancelled -> Filled`, regression of cum fill, out-of-order higher fills, old-revision late fills, overfill, missed revisions and invalid terminal status all enter reconciliation. Fill aggregation B2/B4 must be computed from de-duplicated trade facts, not order-status events alone.

## Execution identity and collision

`OmsExecutionContract.Compare(persisted, incoming)` classifies `New`, `Duplicate`, `Collision` by full `ScopedExecutionId` plus immutable execution payload (local order, quantity, unit, price, timestamp). A duplicate is not counted twice; same scoped exec ID with different order/payload is an **integrity collision**, not an override. Time/quantity differences caused by venue corrections/busts require separately versioned adjustment events in B2/B4 (not an in-place mutation of an execution). The pure comparer provides deterministic rules; it is **not** a concurrent uniqueness store.

## Baseline findings / deferred blockers (source-audited)

1. `src/TradeOps.Domain/Entities/Order.cs` has GUID `Id`, flat `ClientOrderId`/`ExchangeOrderId`, no tenant/account/environment, command or revision fields. Legacy `OrderStatus` has no pending cancel/replace/expired phases. `src/TradeOps.Application/Services/OrderStateMachine.cs` is a separate mutable implementation; B1 contracts **do not replace or wire into it**.
2. `src/TradeOps.Infrastructure/Persistence/Configurations/OrderConfiguration.cs` currently enforces a **global unique `ClientOrderId`**. `FillConfiguration.cs` enforces a **global unique `ExchangeFillId`**. This fails genuine multi-account uniqueness and cannot be fixed without EF schema/data backfill and B2 migration approval. Do not assume current DB is scope-safe.
3. `OrderManager.cs` produces one deterministic label per TradingSignal and uses `GetByClientOrderIdAsync` globally. `ExchangeEventProcessor.cs` and `OrderReconciliationService.cs` also correlate by bare labels/exchange IDs; they cannot safely process simultaneous multi-account streams. Existing `OrderReconciliationService` maps missing order lookup to `Unknown` (better than false cancel), but a missing lookup alone does not resolve an unknown transmission.
4. Deribit testnet HTTP places with `label = ClientOrderId`, recovery uses `private/get_order_state_by_label` with currency and optional instrument. Private WS parser maps `order_id`, `label`, `trade_id` to flat application events, without stream epoch/sequence/account scope. FIX #40 still requires explicit tag 11/41/17/37 normalization and retry/sequence semantics. Do not equate venue adapter presence with production readiness.
5. Repo `.github/workflows/build.yml` installs **.NET 8** while `TradeOps.Domain.csproj` and `TradeOps.UnitTests.csproj` target **net10.0** on the pinned baseline. Existing CI may fail restore with SDK compatibility error before B1 tests; G0 owns baseline CI diagnosis and correction. Worker does not change common workflow configuration.

## Integration gates for B2 and B4

**B2 durable journal:** add transactional account-scoped identities, local version CAS, per-command stable ID, distinct idempotent attempt states, transactional outbox and inbox, uniqueness on full scope/namespace and a typed model version discriminator; validate/reject historical duplicates before migrating indexes. Persist intent and guaranteed unsent state atomically *before* gateway dispatch. A stale gateway must not physical-send under old fencing; executor and secret custody are outside B1.

**B4 reconciliation:** block risk-increasing orders on epoch change/unknown state; obtain coherent snapshots **including closed order history, fills, positions and balances**, covering disconnect gap; replay deduplicated events; match by typed scope/validated stable remote mappings. Empty open-orders result does not mean cancelled. Clear recovery gate only after all conflicting terminal reports, overfills, stale versions and unknown order outcomes are resolved.

**Adapter certification:** prove per-venue uniqueness domain for client IDs/execution IDs, restart/session reset semantics, cancellation outcomes, revision semantics, size units, precision and late fill behavior; qualify Deribit, CoinCall and other venues independently. No real network order entry in this PR. MAINNET STRICT READ-ONLY is unchanged.

## Deterministic checks

`dotnet test tests/TradeOps.UnitTests/TradeOps.UnitTests.csproj --configuration Release --filter "FullyQualifiedName~OmsIdentityContractTests|FullyQualifiedName~OmsLifecycleContractTests"`

`dotnet build TradeOps.sln --configuration Release`

Both require .NET 10 SDK, compatible package restore and ordinary CI environment. Current source-only B1 does not claim venue connectivity, durable persistence, producer ordering guarantee, FIX transport or end-to-end multi-account correctness. Merge acceptance remains blocked until G0 audit and orchestrator review.
