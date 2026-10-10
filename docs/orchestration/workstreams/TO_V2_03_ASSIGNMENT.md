# TO-V2-03 — .NET 10 Dual-Service HTTP Contract Verification (OFFLINE)

**ASSIGNMENT REGISTERED / NOT STARTED — separate worker, no worker merge.**

## GitHub source of truth

- Repository to change: **PRIVATE** `avysotsky/TradeOps-v2`.
- **Existing assigned branch:** `TradeOps/to-v2-03-dotnet10-dual-http-contract`; created by Development Orchestrator from TradeOps-v2 `main=bab7b5c48bbb30677f7747fe2445c2673fd663c5` on 2026-10-10. **Never create another branch for this task**.
- **READ-ONLY second repository:** PRIVATE `avysotsky/DocFlow-v2`, current `main=3f09e91ba356fb367248ef1b48a56b19d9fdae97` (.NET 10, DF-V2-06 merged). No writes to DocFlow-v2 or archived/public repos.
- At registration TradeOps-v2 has **no open PR**; an orchestrator Draft PR for this assignment will be opened in this branch. Recheck exact HEAD, both current mains, compare, open PRs and historical Actions in GitHub before changes; do not assume these SHAs remain current.
- TradeOps-v2 TO-V2-02 [PR #4](https://github.com/avysotsky/TradeOps-v2/pull/4) merged with .NET10 SDK global.json 10.0.401, 15/15 builds, selected mock-only xUnit 79/79 PASS on owner Windows 10; this was **not** whole-suite/real dual-service evidence.
- Older MIG-02 `docs/orchestration/MIG02_MIGRATION_ACCEPTANCE.md` / `tools/MigrationSmoke` verified the historic **Python/FastAPI DocFlow v1 source-equivalent** host with the real TradeOps HTTP adapter, running old .NET 8. It does **not** prove current native DocFlow.Api .NET10 integration. Do not replace or rewrite past evidence; this workstream is a fresh present-day offline native-host compatibility gate.
- Owner machine: Windows 10, Visual Studio 2026, checkouts `D:\Projects\TradeOps-v2` and `D:\Projects\DocFlow-v2`, `D:\DotNet\dotnet.exe` SDK 10.0.401, Python 3.11.5. Private repos are separate but may coexist as sibling paths.
- GitHub Actions are **OFF** in BOTH private repos by owner policy; historical successful runs were on older commits and are not new-head proof.

## Hard prohibitions (do not negotiate by weakening a test)

**NEVER ENABLE, DISPATCH, RERUN, EDIT OR ADD `.github/workflows/**`, or use a hosted/paid replacement for Actions.** NuGet publication/private package ACLs/registry rights remain **PUBLISH BLOCKED / LICENSE APPROVAL BLOCKED**; don't publish, use private credentials/PAT, invent a feed or package, vendor DocFlow code, or alter source history.

**NO REAL BROKER, EXCHANGE, LLM OR DATABASE NETWORK.** No real order placement/cancellation, no production DB migrations or storage changes, no production TradeOps API/Worker startup, no provider/testnet/live host calls, no actual external OIDC authority. Do not run provider smoke executables or `docker compose up`. Do not expose either HTTP service publicly. DocFlow.Api has *legacy DocumentsController routes without mandatory auth* and must never be publicly bound as part of this task. Do not log JWT, secrets, raw document/transcript or actual client content.

**NO IN-PROCESS ADAPTER IMPLEMENTATION** (TO-V2-01B separately gated by immutable private packages and release rights); this is **HTTP boundary** verification only. Preserve existing TradeOps port contract and 1 MiB/2 MiB payload limits; no refactoring trading/risk/reconciliation/EF logic.

## Objective

Prove at the owner-selected .NET10 commits, with both **real C# implementation code paths** linked from their separate private checkouts:

```
TradeOps.Application.IDocFlowExtractionPort
 -> real TradeOps.Infrastructure.DocFlow.DocFlowHttpExtractionAdapter
 -> isolated ASP.NET Core in-memory TestServer hosting real DocFlow.Api POST /api/v1/extractions
 -> real DocFlow.Extraction.Application.DocFlowExtractionFacade
 -> real DocFlow.Normalization and DocFlow.SchemaExtraction
 -> explicitly injected deterministic test-only IExtractionBackendFactory
```

Do not replace either of the two real ports/adapters/endpoint/facade with mocked DTO outputs. Only **external edges** may be faked: in-memory HTTP host, OIDC signing/JWKS, backend factory and provider result. No PostgreSQL/LLM or external network. This distinction is essential; the older Python MIG-02 harness is not the test target.

### Discovery and test design

1. Inspect `docs/orchestration/V2_ACTIVE_ROADMAP.md`, `DUAL_TOPOLOGY_ARCHITECTURE_PLAN.md`, `MIG02_MIGRATION_ACCEPTANCE.md`, `ARCH03_DOCFLOW_HTTP_ADAPTER.md`, `docs/orchestration/workstreams/TO_V2_01B0_INPROCESS_ADAPTER_DESIGN.md`, TradeOps port + HTTP adapter + registration + tests and current 15-project .NET10 matrix.
2. In DocFlow-v2 read `src/DocFlow.Api/{Program.cs,Extraction/ExtractionHttpEndpoint.cs,Extraction/ExtractionHttpOptions.cs}`, `tests/DocFlow.Api.Extraction.Tests/Program.cs`, extraction facade/backend factory, canonical normalization tests and 2.0.0-preview packaging policy. DocFlow native API needs host-injected `IExtractionBackendFactory`, valid service JWT, HTTPS request scheme, and test-only configuration. Startup currently configures DbContext and LocalFileStorage: **verify** that WAF/TestServer fixture does not connect to DB or touch non-temporary real storage. Use a unique temporary sandbox for test-only storage/config and no real DB connection; clean it afterward.
3. Pick a durable harness strategy. Prefer a **portable, locally executable offline harness** checked into TradeOps-v2 with PowerShell wrapper that locates sibling DocFlow-v2 checkout and constructs a temporary test-only project/solution with project references to the two private source trees. Avoid committing source copies, hard-coded cross-repo `.csproj` references that break normal `TradeOps.sln` builds, or changing DocFlow-v2. A different clean approach is acceptable if it exercises both real native .NET codebases without network.
4. **Fail closed** on wrong SHA/branch or dirty tracked working trees; consider ensuring both main exact SHAs for repeatability (or explicit later reviewed SHAs). At least report and verify exact TradeOps + DocFlow commits in the test evidence, and include proof that only .NET10 assemblies were built.
5. If you cannot host DocFlow safely without binding a socket or accidental DB mutations, **stop, document the blocker and create a proposed in-memory TestServer-only route**. Never start a production host or change its safety controls just to make the smoke green.

### Required offline coverage

- Successful real round-trip through TradeOps adapter and real DocFlow host with deterministic backend; preservation of `schemaVersion:1`, `normalizedDocument.document_id`, `fingerprint`, `segment_id`, participants/provenance, complete `structuredResult` including confidence and `validation_status`. Compare to **direct** native DocFlow facade for the exact same input; use deep JSON equivalence and exact canonical identities, not string-field cherry-picking.
- UTF-8 / non-ASCII (Cyrillic, diacritics, separator and optional emoji) through actual wire serialization; verify no old CP1252/console corruption.
- Well-formed schema-invalid backend output returns contractually invalid extraction status without escaping into an unhandled exception; unsupported schemas and invalid selectors must fail closed.
- Security boundary: missing/invalid JWT and wrong scope/client identity rejected before backend dispatch; synthetic local valid service JWT accepted without real OIDC network; HTTPS test scheme required. Do not weaken token auth or alter issuer requirements. Send client token in test fixture via TestServer-injected HttpClient only.
- Version mismatch, malformed/oversized request/response, cancellation/timeout and non-success backend/HTTP error; verify TradeOps error is bounded/sanitized and never logs document, JWT or provider error. No silent retries/provider fallback. Respect 1MiB request and 2MiB response caps, protect resource budgets in test data.
- Independence: when HTTP route disabled or backend unregistered, fail closed without replacing it with a fake success. No broker/order/mutation calls and no real host network port.
- Negative outcome may reveal intentional HTTP vs native differences; document accurately rather than changing public contracts or equality semantics outside workstream.

### Deliverables

1. Add **one** isolated test harness under `tools` or `tests` (code + any test project) using the real code paths, strictly safe injected fixture; keep current TradeOps `TradeOps.sln` usable and existing 15 core project inventory untouched unless orchestrator separately authorizes a narrow inventory/validator update. If adding a separate test csproj becomes project #16, document it and ensure existing `scripts/to-v2-02-validate-local.ps1` is either appropriately updated to be compatible or the new harness is outside standard 15-project glob (e.g. temporary at run time); **never silently break** completed TO-V2-02 validation.
2. Add `scripts/to-v2-03-validate-local.ps1` for owner Windows10/VS2026/.NET SDK10.0.401. Ensure clean-checkout, exact-commit guard, no Actions and no production starts; capture per-case pass/fail counts and exit code. Package restoration from public NuGet.org may be necessary and is not a provider call; no private registry publish.
3. Add `docs/orchestration/workstreams/TO_V2_03_STATUS.md` with exact source SHAs, inventory, test evidence or **NOT RUN**, cross-repo dependency shape, fixed backend/auth fixture, known missing external evidence, security/side effect checks, and next steps.
4. Add `docs/orchestration/workstreams/TO_V2_03_CONTRACT_MATRIX.md` defining what was *actually tested* across TradeOps adapter, real DocFlow.Api endpoint and native facade, expected HTTP error/cancellation behavior, and any discovered gaps; do not falsify claims of provider, auth/deployment, DB or archive readiness.
5. Update existing Draft PR #5 (or actual PR number if different) with exact final head, files/compare, case counts, owner PowerShell commands, C# SDK and honest local test status.
6. Worker **must not merge**. Orchestrator will independently review and owner will execute the exact-HEAD offline verification if worker cannot.

**GH Actions OFF; NO WORKFLOW YAML; NO LIVE TRADES; NO PRODUCTION API/DB; NO NUGET PUBLISH; NO MERGE.**
