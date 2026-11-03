# TO-V2-18 — Fail-closed synthetic reconciliation canary inside the REAL Worker

**Repository:** PRIVATE `avysotsky/TradeOps-v2`
**Parent:** TO-V2-17 exact independent offline-qualified HEAD
`3b4c38b7b0c1a3a2a5dd419a19c5eab175c07d51`.
**Workstream branch:** `TradeOps/to-v2-18-shadow-reconciliation-runtime-canary`
**Merge/release:** unauthorized. Keep Draft and preserve all accepted parents.

## Scope: in-process, synthetic-only shadow canary

The previous TO-V2-16 decision engine and TO-V2-17 read-only Mock
and IBKR offline evidence adapters are now exercised from the
**actual compiled `TradeOps.Worker` process** via an independent,
opt-in `IHostedService`.

**Important classification:** this is a *synthetic runtime canary*,
NOT yet a live-order reconciliation shadow. The legacy `Orders`
schema has no reliable venue/account/qualified-instrument identity,
and legacy `MockExchangeClient.GetOrderAsync()` advances fills
on reads. Treating that state as authenticated, immutable broker
evidence would create a false sense of correctness. This milestone
does **not** compare production order rows, persist shadow decisions,
or replace `OrderReconciliationService`.

## Startup hard gate and defaults

`src/TradeOps.Worker/appsettings.json` declares:

```json
"ReconciliationShadow": { "Enabled": false }
```

The canary is registered ONLY when all of these are true:

1. `ReconciliationShadow:Enabled=true` is explicitly supplied.
2. `Exchange:Provider=Mock` exactly; any Paper/live provider
   is rejected before host startup.
3. Environment variable `TRADEOPS_RECON_SHADOW_CONFIRM` equals
   `SYNTHETIC_MOCK_IBKR_ONLY`, exactly and case-sensitively.

Failing any of these conditions while enabled terminates startup
without starting the new hosted service. With `Enabled=false`,
existing Worker behavior and DI are unchanged. Do not enable this
on any live/Paper broker application.

## One-shot canary and observable proof

`OfflineReconciliationShadowCanary.RunAsync()` executes exactly
six deterministic, wholly local assessments with two source adapters,
passing them through the SAME Application `BrokerReconciliationDecisionEngine`:

| Synthetic scenario | Mock | IBKR offline |
| --- | --- | --- |
| Two identified executions total the full requested amount | Candidate Filled | Candidate Filled |
| Complete account-order snapshot with no matching order | Unresolved NotFound | Unresolved NotFound |
| Missing terminal pagination / incomplete account-order list | Unresolved Incomplete | Unresolved Incomplete |

No API token, actual order identity, network request, database connection,
`IExchangeClient`, repository, HTTP client or broker mutation participates.
The new hosted service obtains only the decision engine and logger and
runs this canary once at startup; there is **no recurring polling,
no additional background thread**, and no trading-state mutation.

The service logs ONLY a static aggregate marker, never an account key,
raw order, execution ID, instrument, credential, SQL or connection string:

```text
TRADEOPS SHADOW RECONCILIATION CANARY PASS providers=2 evaluations=6 candidates=2 unresolved=4 db_writes=0 broker_requests=0 mode=synthetic_only
```

The `db_writes=0` refers specifically to **the canary**.
The existing primary recovery Worker still performs its pre-existing
database operations; the marker is NOT a no-writes certification
for the whole Worker process.

The canary throws if either source returns the wrong evidence
availability/decision/quantity. An exception does not produce a
PASS marker. No non-executable `Candidate` is ever forwarded to
any database or order transport.

## Automated real PostgreSQL/API/Worker acceptance

`scripts/ibkr/to-v2-14-run-real-postgres-acceptance.sh` retains
the existing owner-executed real PostgreSQL 17 Npgsql journal,
API HTTP, durable API restart, Worker boot, SQL and cleanup gates.
For the disposable **Mock** Worker only it supplies the two
explicit canary opt-in settings and verifies the aggregate canary
log marker before declaring Worker PASS:

```text
TRADEOPS REAL SHADOW RECONCILIATION PASS providers=2 evaluations=6 candidates=2 unresolved=4 db_writes=0 broker_requests=0
TRADEOPS REAL WORKER START PASS
...
TRADEOPS REAL POSTGRES ACCEPTANCE PASS ... cleanup=PASS broker_requests=0
```

The API process is not opted in. All real PostgreSQL artifacts are
ephemeral and bound to loopback as in TO-V2-14/15. No user secrets
or existing databases are modified. Bash `bash -n` structural tests
still guard the exact acceptance script.

**Qualification gates:**

1. Separate Draft PR stacked on accepted TO-V2-17, exact SHA.
2. Independent WSL2 restore/build/xUnit ALL PASS, zero skipped,
   six cleanup groups true.
3. Owner runs the Windows launcher with **explicit `-Branch`**
   pointing to TO-V2-18, exact qualified SHA; 0 exit and
   `TRADEOPS REAL SHADOW RECONCILIATION PASS` + full final
   PostgreSQL 17 acceptance PASS.
4. Evidence comment on this PR only after real owner transcript;
   never claim real runtime PASS from unit tests.

## Next actual live-order shadow stage (not authorized yet)

Persist a validated provider/account/contract identity and stable
last-observation sequence with each order, prevent concurrent
legacy/new writers, implement authenticated read-only broker
source adapters, classify IBKR 503 separately from 404,
enforce instrument multipliers and amounts, and provide an
opt-in account-specific **read-only live evidence** shadow comparison
against persisted order rows. Only after separate transactional
acceptance and explicit signoff can a single authoritative engine
replace the legacy reconciliation policy.

No real/Paper trading, no broker fetches, no production order
updates, no merge or package publication is introduced in TO-V2-18.
