# ARCH-02 — TradeOps Research / Rebalance / Risk / Dry-Run HTTP Host

## State

INTEGRATED — PR #72 merged, exact-head build successful; post-merge CI not independently verified

## Repository

- Repository: `avysotsky/TradeOps`
- Assigned branch: `TradeOps/arch02-research-risk-http-host`
- Initial baseline: `c13e229835bd17e49430afe7d773c2e618e6e5d4`
- DocFlow ARCH-01 integrated: PR #8, merge `d6de1b5168c156b107cb3c3d71ef29983401ad40`.
- Canonical roadmap: `docs/orchestration/DUAL_TOPOLOGY_ARCHITECTURE_PLAN.md`

## Goal

Extend the **existing** TradeOps.Api host to expose the already-integrated deterministic ResearchDecision / rebalance / risk-preview / execution-dry-run application flows via thin HTTP endpoints. Do not duplicate pipeline implementation inside controllers or enable broker/order mutation.

## Candidate API v1

```http
POST /api/v1/research/decisions
POST /api/v1/rebalance/preview
POST /api/v1/risk/preview
POST /api/v1/execution/dry-run
```

Exact request/response DTO shapes must be based on existing frozen application models and validated fixtures; don't freeze speculative contracts before inspecting production services. Avoid large-bang changes.

## Initial live review (mandatory)

1. Re-fetch live TradeOps and DocFlow main HEAD, assigned branch HEAD, compare, relevant PRs, exact CI, and existing routes.
2. Read `docs/orchestration/DUAL_TOPOLOGY_ARCHITECTURE_PLAN.md`, `docs/orchestration/ORCHESTRATION.md`, `docs/orchestration/WORKSTREAM_PROTOCOL.md`, `docs/orchestration/VS17_EXECUTION_DRY_RUN.md`.
3. Inspect `src/TradeOps.Api`, existing application DI/host composition, research decision models, VS-16 risk preview and VS-17 dry-run harness implementations.
4. Identify which parts of the current demo composition need narrow reusable application-service extraction. Prefer production application code reuse over new domain models.
5. Document exact endpoint contract and impacted files before implementing. Never assume an endpoint can directly accept an entire demo filesystem manifest.

## Mandatory boundaries

- Reuse existing TradeOps.Application logic, risk checks, deterministic identities and RebalanceOrderIntent semantics.
- API handlers are transport adapters, not strategy or execution implementations.
- Preserve existing legacy TradeOps.Api endpoints and existing CLI/demo regression harness.
- No DocFlow normalization / provider calls inside TradeOps; ARCH-03 implements future HTTP adapter separately.
- No `PlaceOrderAsync`, `CancelOrderAsync`, `OrderManager.ExecuteSignalAsync`, signal/order repository writes, database mutations, or broker submission.
- Risk must fail closed on invalid/mismatched intent or disallowed policy.
- Structured errors must be sanitized; validate bounded requests; no credentials in DTOs/logs.
- Maintain exact public/shared contract compatibility unless orchestrator explicitly approves an additive versioned DTO.

## Acceptance

1. Existing TradeOps.Api runs with the new additive HTTP routes.
2. Research decision, rebalance preview, risk preview and dry-run route behavior reuse existing validated application semantics; do not silently introduce competing business logic.
3. Offline deterministic fixtures demonstrate direct-vs-HTTP semantic equivalence for every newly exposed route.
4. Rejected risk produces no prepared order identity. Allowed dry-run contains production-style client order ID and explicit `mutationPerformed=false`, `brokerRequestSent=false`, `persistencePerformed=false`.
5. No broker mutation, no persistence, no DocFlow ingestion implementation, and no real IBKR account requirement.
6. Existing full TradeOps tests, API/PostgreSQL smoke, signed webhook demo, deployment/Docker validation pass with zero build warnings.
7. Exact-head CI and CodeQL pass; privacy/secret scan passes; no client/portfolio secrets in test fixtures.
8. No changes to frozen domain contracts without a documented compatibility decision.

## Completion

Update this document with exact HEAD, changed-file ownership, tests, exact CI, contracts, known gaps and next integration action. Create **draft PR only**, then stop for orchestrator review. Do not merge from worker chat.

## Live discovery checkpoint — 2026-10-08

TradeOps main observed: `d1f0ed6c2d42562e75c0653c390e1a34b0c5fcb6`.
DocFlow main observed: `d6de1b5168c156b107cb3c3d71ef29983401ad40`.
Initial ARCH-02 branch was ahead 1 / behind 1 because the orchestrator status commit advanced main after branch registration. No overlapping production files were changed.

Confirmed code:
- Existing `src/TradeOps.Api/Program.cs` registers the production `IRiskEngine` with database-backed `IRiskControlService`; the web host runs database migrations on startup.
- `src/TradeOps.Application/Services/RiskEngine.cs` invokes `IRiskControlService.RecordRejectionAsync` when policy rejects a signal. Therefore **injecting the normal production risk engine in a preview endpoint is not read-only**.
- VS-16 demo uses a non-mutating risk-control adapter and isolated position exchange-client adapter; its `RebalanceRiskPreviewSignalProjector` supplies stable `TradingSignal` identity.
- VS-17 dry-run service in the transcript demo owns an additive execution envelope with production `ClientOrderIdGenerator`.
- TradeOps.Api already exists; no new API project or duplicate host should be created.

## Required correction before implementing risk HTTP

Refactor/share the **pure** preview composition into the appropriate reusable application boundary, or isolate it behind a DI-registered query service with explicitly non-mutating `IRiskControlService` and read-only portfolio snapshot adapter. Do not route any HTTP preview through the standard DI-registered, database-backed `IRiskControlService` that records risk rejection. For the same reason, do not mutate operational run state, exchange state, orders, alerts, or persistence while previewing.

All four endpoints must be driven by bounded request DTOs (not local filesystem paths). A proposed plan/risk/dry-run request may carry only validated snapshot data and stable IDs. A real-provider source acquisition or DocFlow transport is not part of ARCH-02.

## Current status and gate

No HTTP endpoint implementation has been committed in this discovery checkpoint. No tests or CI for ARCH-02 can yet be claimed. The next code slice must first provide safe reusable preview services and fixture tests demonstrating **zero calls** to rejection-recording, storage and broker mutation interfaces. Then thin HTTP routing and direct-vs-HTTP parity tests can be added. Do not mark ready for integration until all four contract surfaces are implemented and CI passes.

## Risk-preview HTTP slice — 2026-10-08

Production application + unit-test slice at `6f26abfe94b50aa5c7b36d3e2ff52f0aac28e3db` passed build CI `37794873196` SUCCESS.

The following incremental HTTP slice is committed:
- `src/TradeOps.Api/Controllers/ResearchRiskPreviewController.cs`
- `tests/TradeOps.UnitTests/ResearchRiskPreviewControllerTests.cs`
- HTTP endpoint: `POST /api/v1/risk/preview`; requires existing `[OperatorApiKey]` and `ResearchPreview:Enabled=true` (disabled by default).
- The controller constructs only a transient signal, executes `NonMutatingRiskPreview.CheckAsync` with provided snapshots, and returns risk decision.
- No database-backed IRiskControlService is injected into this endpoint and no broker invocation or order persistence occurs.
- Validated test cases: disabled endpoint, allowed risk, rejected risk, invalid quantity. Tests are committed but their exact-head CI remains PENDING.
- **Security review required before enabling endpoint**: confirm operator authentication is enabled in deployed settings (existing operator auth may itself be optional), and trust/authenticate submitted risk snapshots. An attacker-supplied snapshot must not be mistaken for authoritative live risk approval.
- HTTP risk response is informational only; it **must never authorize production order execution**.
- Other ARCH-02 routes (research/rebalance/dry-run) are not yet implemented. No integration or merge before all gates.

## Four-route implementation checkpoint — 2026-10-08

Added query-only HTTP surfaces:
- `POST /api/v1/research/decisions` — uses existing `ResearchDecisionValidator.Validate` (returns canonical normalized decision / validation result).
- `POST /api/v1/rebalance/preview` — calls the existing `PortfolioRebalancePlanner.Plan` with request-local `ResearchDecision`, `PortfolioSnapshot`, optional reference price and constraints.
- `POST /api/v1/risk/preview` — uses `NonMutatingRiskPreview.CheckAsync` against request-local risk and position snapshots.
- `POST /api/v1/execution/dry-run` — uses new `RebalanceExecutionDryRunPreview.RunAsync`, which reuses production `RiskEngine`, canonical application signal projection and `ClientOrderIdGenerator`; emitted result has no mutation, broker request or persistence.

All new routes are `[OperatorApiKey]` method-gated and fail closed unless both `ResearchPreview:Enabled=true` and `OperatorApi:Authentication:Enabled=true`. There is no database access or broker dependency in their logic. These are **simulated, caller-supplied snapshot previews**, not authorization to trade. Any future executable risk decision must be recomputed using trustworthy live controls.

Extracted canonical `RebalancePreviewSignalProjector` into TradeOps.Application and changed existing VS-16 demo projector to delegate to it, preserving ID source and hash semantics. This avoids two independent deterministic signal-ID algorithms.

Added tests for reusable risk allowed/blocked/position limit, research validation/rebalance preview, execution dry-run allowed/rejected/invalid, stable client order IDs and legacy VS-16 projector parity, endpoint disabled/auth gate and direct-vs-controller equivalence. No live broker/provider credentials.

Validation evidence:
- Earlier risk adapter build `37794873196` SUCCESS.
- Risk endpoint build `37795989073` FAILED because attribute placement was invalid; fixed before this checkpoint.
- Subsequent exact-HEAD build `37796349238` SUCCESS.
- Final four-route implementation HEAD before this docs update: `a109ff9fc81ae5b916cb0bf451b665d8ce69224d`.
- **Exact-head CI for final four-route implementation remains pending and must be checked before merge.**
- Broader HTTP integration and threat-model review are still required; controller-level parity tests alone do not prove full hosted ASP.NET routing, serializer and authorization behavior.
- Branch currently trails a main orchestration-only commit; integrate that documentation change or confirm clean merge before final review.
- Real Groq dry-run and IBKR Paper live broker acceptance not included.

Security considerations:
- The legacy operator API auth filter is configurable; disabling it must not expose these new endpoints.
- Request-local risk controls are untrusted and have no power to submit trades.
- Do not add `OrderManager`, `SignalExecutionService`, order repositories, execution triggers or provider credentials to these routes.

## Integrated API expansion (same ARCH-02 workstream)

The existing `POST /api/v1/research/decisions` validates a supplied decision using the production validator. In addition, `POST /api/v1/research/decisions/derive` creates a real deterministic `ResearchDecision` from two caller-supplied canonical DocFlow normalized/structured artifacts and an explicit research policy through `TranscriptResearchDecisionDemoService.Run`, without embedding DocFlow normalization or invoking a provider. The derivation endpoint returns bounded decision, assessment and policy fingerprint rather than raw artifacts. Its fixture test compares the output decision against direct application service invocation and tests malformed input rejection.

Added hosted ASP.NET HTTP integration coverage in `ResearchPreviewHttpIntegrationTests` using isolated PostgreSQL test database and real authentication filter for research-validation, rebalance and dry-run routes, including serializer and no-mutation flags. Existing seeded tests remain synthetic and offline. This test runs when the existing `TRADEOPS_TEST_POSTGRES_ADMIN` is set or GitHub Actions PostgreSQL service is present.

Final implementation HEAD before this documentation update: `f8df0f9e678c0969de4964d273ebb55a9d276676`. Exact-head CI pending. Review must verify the HTTP integration test and final full suite before integration; no merge from this worker.

## Orchestrator integration verdict — 2026-10-08

- PR #72: https://github.com/avysotsky/TradeOps/pull/72 — merged (squash).
- Final reviewed branch HEAD: `c1dd06bb4aa21912db126681a7d421d97a76daaf`.
- Exact final-head PR CI: `37798316067` — SUCCESS (full build/unit tests, API/PostgreSQL smoke and deployment pipeline).
- Other same-head runs: push build `37798306782` — SUCCESS; dynamic PR #72 check `37798308432` — SUCCESS.
- Squash merge commit in TradeOps main: `c0d589c91d49d4cffda1deb99dc9ba0ea4e4ca3d`.
- Post-merge push CI has not yet been separately verified.
- Root-cause/fix log: initial API controller attribute placement error CS0592 fixed; hosted JSON model binding of multi-constructor RiskControlSnapshot fixed with HTTP-only `ResearchRiskControlsRequest`; research parity test corrected to compare metadata structurally.
- Final endpoints: research validation, deterministic research derivation, rebalance preview, risk preview, execution dry-run. All are additive, opt-in, protected by operator authentication and do not authorize live trades.
- Existing VS-16 deterministic signal IDs shared with application projector; no new broker mutators, order persistence, risk rejection recording or DocFlow ingestion implementation.
- ARCH-03 may proceed to the TradeOps-owned port and HTTP DocFlow adapter. Do not treat client-supplied preview snapshots as authoritative production risk approval.
