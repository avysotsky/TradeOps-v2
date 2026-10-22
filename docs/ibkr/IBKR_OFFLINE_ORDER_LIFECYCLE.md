# TO-V2-07 — IBKR account-free offline order lifecycle

**Scope:** deterministic, in-process order lifecycle simulator under
`src/TradeOps.Infrastructure/Exchange/Ibkr/IbkrOfflineOrderLifecycle.cs`.
This feature adds no broker transport, live/order endpoints, credentials,
account connectivity, or order-placement/cancellation capability.

## Design

This module is deliberately **not** wired into `IbkrPaperExchangeClient`
or dependency injection. `IbkrPaper` continues to deny place/cancel locally.
The model consumes **synthetic, trusted test trace events only**, and cannot
send an HTTP request. It is a state machine for order reliability review,
not an implementation of IBKR's production response schema.

- `CreateDeterministicCoid(scope, requestKey)` produces a stable, fixed-size
  lowercase SHA-256-derived client order identifier. Real production
  uniqueness, allowable character set and length must be validated against
  the selected IBKR API before introducing authenticated Paper placement.
- `Register(intent)` saves an in-memory business intent; duplicate identical
  registration is harmless, while the same cOID with different parameters
  is blocked. **Persistence/restart is not implemented**; this cannot provide
  durable exactly-once submission guarantees.
- Preflight must be explicitly approved before `SubmissionStarted`.
  Preflight rejection is terminal and cannot start a submission.
- `ReplyRequired(replyId)` is **not** an order acknowledgement. Explicit
  `ReplyConfirmed` is modeled before the next reply or final ACK. Replayed
  reply IDs and chains exceeding three steps fail closed.
- A simulated broker ACK binds a broker order ID to the cOID. A mismatched
  order ID always fails; late matching ACKs cannot regress partial or
  terminal execution.
- HTTP 401/403/429/5xx, timeout and disconnect move an in-flight attempt
  into `Unknown`. `ReconciliationNotFound` does not authorize re-submission.
  Only an explicit trusted acknowledgement or confirmed reconciliation
  can resolve ambiguity. Retry/backoff is **not automated**.
- Fill events report **cumulative** quantity and must be monotonic, nonnegative
  and no greater than requested quantity. Replayed cumulative values do
  not count twice. Partial fill followed by cancel and late fill is modeled.
- A local cancel request is idempotent. Cancel ambiguity also requires
  reconciliation; a late cancel ACK cannot roll back a completed fill.

## Test examples

Run (with .NET 10):

```bash
dotnet test tests/TradeOps.UnitTests/TradeOps.UnitTests.csproj --filter FullyQualifiedName~IbkrOfflineOrderLifecycleTests
```

Tests cover deterministic cOID, collision, preflight, multiple reply
steps, duplicate and delayed ACK, 401/403/429/5xx, timeout, reconnect,
unknown/not-found, order ID mismatch, cumulative fill monotonicity,
cancel/fill races, and deterministic in-memory event trace replay.

## Important boundaries

- All account/order identifiers in test cases are invented fixtures.
- No credentials, trading access, market data, TWS session or Gateway are used.
- No guarantee of actual IBKR compatibility or support for actual order
  placement is claimed. Authenticated Paper validation is separately
  required before any production integration.
- No executable IBKR mutation capability is enabled by this work.
- The initial model does not parse raw IBKR Web API JSON, implement
  durable storage, concurrent order processing, cryptographic audit trails,
  per-account cOID namespaces, contract metadata, broker-specific pacing
  policies or production retry/reconciliation. These must be separate,
  reviewed milestones, not implicit capabilities of this model.

## Automated qualification

This work is stacked on **TO-V2-06 Draft PR #8** rather than merged main.
For the new Draft PR check its exact current HEAD against the independent
WSL2 runner result:

`avysotsky/TradeOps-Relay-Queue/autotest-results/tradeops/pr-<PR>/<SHA>.json`

The runner must report restore/build/tests and complete cleanup PASS at
the current SHA. Do not treat a successful PR #8 result as proof for this
change. No merge or NuGet publication without owner approval.
