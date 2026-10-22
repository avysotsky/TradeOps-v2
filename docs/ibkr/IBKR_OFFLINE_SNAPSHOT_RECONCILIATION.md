# TO-V2-09 — IBKR offline snapshot reconciliation (account-free)

**Repository:** PRIVATE `avysotsky/TradeOps-v2`
**Stack:** TO-V2-09 based on TO-V2-08 Draft PR #10, which is based
on TO-V2-07 Draft PR #9 and TO-V2-06 Draft PR #8.
**Scope:** synthetic normalized broker evidence only, with **no** IBKR
login, account, outbound HTTP, live/Paper order placement, cancellation,
confirmation, TWS/Gateway or market data.

## Problem / safety invariant

After a submission timeout, dropped reply, disconnect or ambiguous cancel
outcome, internal state becomes `Unknown`. Re-sending the order simply
because it cannot be found can create duplicates. Reconciliation must
require *identity-consistent*, *complete* broker state and execution
evidence before clearing `Unknown`.

A complete, credible "not found" result **still does not** authorize
another submission. The existing `IbkrPaperExchangeClient` place/cancel
methods and capability flags remain read-only and unchanged.

## Implementation

`src/TradeOps.Infrastructure/Exchange/Ibkr/IbkrOfflineSnapshotReconciler.cs`

- Entirely in-memory and **not registered in the runtime service container**.
  No HTTP client, no endpoints, no secrets and no broker commands.
- Consumes explicitly normalized synthetic `IbkrOfflineEvidenceSnapshot`;
  it is **not** a parser for raw IBKR Web API JSON. Such a transport/parser
  requires a later broker-account qualification and separate review.
- Requires the exact target account, positive observation sequence,
  a known local order in `Unknown`, a full order-list coverage marker,
  and for a match, complete execution coverage.
- Correlates BOTH `ClientOrderId` (`cOID`) and `BrokerOrderId`
  when both are known. Rejects mismatched identities, account, symbol,
  side, requested quantity, and repeated order identifiers.
- Execution rows bind their own execution ID, account, client ID and broker
  ID. Identical repeated execution rows are deduplicated; conflicting
  repeats and negative, missing, or excessive sums fail closed.
- Cumulative executed quantity must equal the deduplicated complete
  execution ledger and must never decrease relative to already-known
  local fills.
- `Submitted`, `PreSubmitted`, `PendingSubmit`, `PartiallyFilled`,
  `Filled`, and `Cancelled` are mapped to existing offline lifecycle
  reconciliation events **only** when quantitative and identity
  invariants are satisfied.
- `PendingCancel`, `Rejected`, unknown statuses, incomplete data
  and partial paginated order data remain `Inconclusive`: no mutation
  and no trading action. Absent orders return `NotFound` and remain
  `Unknown`.
- Same sequence and byte-equivalent normalized evidence replays
  idempotently. Older sequence or different evidence with the same
  sequence is rejected.
- This first reconciler resolves an `Unknown` order only. Subsequent
  broker updates after resolution require a later separately designed
  update model. Reconciler instances and lifecycle reducer are
  single-threaded, ephemeral test objects; not durable/thread-safe.

## Offline verification

```bash
dotnet test tests/TradeOps.UnitTests/TradeOps.UnitTests.csproj --filter FullyQualifiedName~IbkrOfflineSnapshotReconcilerTests
```

Tests include a complete empty snapshot, incomplete order pagination,
unconfirmed execution coverage, correct working/partial/filled/cancelled
restoration, same execution ID replay, conflicting duplicate executions,
cross-account or cross-order mismatch, identity and instrument changes,
stale sequences, conflicting same-sequence observations, quantity
regression, absence after a timeout and cancel/fill races.

## Explicitly not implemented

- Authenticated IBKR Web API and execution-report schemas, pagination,
  timestamps/cursors, account permissions, TWS state and exchange support.
- Persistence or exactly-once guarantees across restarts.
- Concurrent/cross-process transaction isolation and durable fill ledger.
- Production reconciliation, automatic retries, second order submission,
  confirmation POST, cancellation DELETE or any live trading.
- Real Paper / live broker verification.

## Qualification and release boundary

The existing Ubuntu WSL2 automation checks every Draft PR HEAD. Find
this PR's **exact current HEAD** test result at:

`avysotsky/TradeOps-Relay-Queue/autotest-results/tradeops/pr-<PR>/<SHA>.json`

Only restore/build/unit tests plus full cleanup PASS qualifies offline
implementation. Earlier PR reports cannot qualify the new head.
No manual relay tasks, GitHub Actions, branch merge, DocFlow changes,
NuGet publication or trading enablement. Owner approval required to merge.
