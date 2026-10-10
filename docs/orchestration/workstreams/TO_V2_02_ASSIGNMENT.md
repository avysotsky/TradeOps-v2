# TO-V2-02 — TradeOps-v2 Full .NET 10 Migration and Local Offline Regression

**REGISTERED / NOT STARTED — independent implementation worker; no worker merge.**

## Registered live GitHub state — 2026-10-10 (Europe/Kyiv)

- Repository: **PRIVATE** `avysotsky/TradeOps-v2`. Never modify archived public TradeOps v1 or DocFlow v1.
- **Existing assigned branch (already created by orchestrator):** `TradeOps/to-v2-02-dotnet10-migration`. **Do not create/switch to another branch or rewrite branch history.**
- Baseline TradeOps-v2 `main` and initial new branch HEAD: `cd12803008b28d24c3db4ca61fb8681859000433`. Read fresh live main + head before all writes.
- DocFlow-v2 `main=3f09e91ba356fb367248ef1b48a56b19d9fdae97`: DF-V2-06 net10 upgrade **merged** by orchestrator via squash of PR #12 from worker HEAD `38defb768cbcc8073b9e579cf196a0c426b2f968`; **read-only** for this worker.
- Owner exact-HEAD Windows offline DF-V2-06 evidence at source HEAD: SDK 10.0.401; 13 C# projects built; 12/212 facade; 9/149 HTTP; 46 ingress; 21 release gate; 116/116 differential; 13 consumer checks and locked offline restore PASS. Full archive .nupkg/.snupkg SHA256 **differed on each pack** although source code/nuspec/README/PDB payload hashes were stable; not bit-reproducible. DocFlow private NuGet publication is **PUBLISH BLOCKED / LICENSE APPROVAL BLOCKED**.
- TradeOps-v2 current configuration: `global.json` SDK `8.0.400` / latestPatch; **15 C# projects** (5 src, 1 test, 9 tools), all currently `net8.0`. `TradeOps.sln` contains 14 of 15: `tools/MigrationSmoke/TradeOps.MigrationSmoke.csproj` must ALSO be inventoried; recheck live. Runtime Dockerfiles are `src/TradeOps.Api/Dockerfile` and `src/TradeOps.Worker/Dockerfile`, both on image family 8.0. Deployment compose files reference these builds.
- Owner's Windows 10 x64: Visual Studio **2026**, checkout root `D:\Projects`; TradeOps `D:\Projects\TradeOps-v2`; `D:\DotNet\dotnet.exe` SDKs 8.0.421 and **10.0.401** with net10 10.0.12 runtime/targeting packs; Python 3.11.5. **Do not pretend to have executed code on owner's computer**.
- At workstream registration, no open TradeOps-v2 PR. GitHub Actions are intentionally disabled by owner in **both PRIVATE repos**.

## Mandatory operating restrictions

**GITHUB ACTIONS OFF.** Absolutely do not enable, dispatch, rerun, schedule, modify or create any `.github/workflows/**` YAML; no cloud CI, paid alternative, GitHub Actions billing/settings, or leveraging old historical green as new-HEAD evidence. GitHub connector use for source changes, draft PR and read-only Actions runs checks is allowed. Confirm no post-commit new run was created.

**ZERO LIVE TRADING / ZERO PROVIDER EGRESS / NO PRODUCTION DATA MUTATION.** Never place real orders, cancel orders, write real DB, run EF migrations against user database, call live brokers/LLMs, export secrets, provision credentials, start demo gateways/smoke tools that can contact exchanges, or launch production API/Worker startup. Use offline mocks/TestServer and test DB isolation only when demonstrated safe. Tests may include existing live-marked cases; explicitly exclude them, with itemized counts and reason. No unreviewed `docker compose up` because it can migrate DB and/or connect to exchanges.

**NO NUGET PUBLISH / NO PACKAGE ACCESS.** No attempt to publish DocFlow packages, add authentication/PAT/secrets, create/modify registry ACLs, source-vendor DocFlow or substitute unauthorized public packages. Do not implement in-process DocFlow adapter as part of this workstream; its G3/G4 gates remain blocked pending actual approved packages/permission and separately assigned TO-V2-01B. The existing HTTP adapter and DTOs must maintain semantics.

## Goal

**Migrate TradeOps-v2 completely from net8.0 to net10.0**, aligning with merged DocFlow-v2 net10 baseline, without changing trading/execution behavior, risk rules, public HTTP/webhook contracts, provider policies, or persistence schema. This is a **target-framework/dependency/Docker/runtime migration**, not a rewrite, feature or release deployment.

### Preflight and inventory (MUST DO FIRST)

1. Read TradeOps and DocFlow current `main` exact SHAs, assigned branch current HEAD + compare, open PRs/workstreams, recent GitHub Actions runs **read-only**; record any changing baseline or overlap before writes.
2. Read `docs/orchestration/V2_WORKSTREAM_REGISTRY.md`, `docs/orchestration/V2_ACTIVE_ROADMAP.md`, `docs/orchestration/DUAL_TOPOLOGY_ARCHITECTURE_PLAN.md`, `docs/orchestration/workstreams/TO_V2_01B0_INPROCESS_ADAPTER_DESIGN.md`, workstream protocol, root README, TradeOps solution and all fifteen project files, both Dockerfiles and active deploy compose entries.
3. Write concise migration matrix listing **all 15** .csproj, `net8.0` TFM, root and direct NuGet dependencies and `TradeOps.sln` membership, plus baseline Docker/runtime/tool version paths. Inspect source references to deprecated net8 APIs, EF Core/Npgsql10 compatibility, test SDK/xunit and third-party provider dependencies.

### Implementation (single substantial workstream)

**A — Project and toolchain uplift**
- Upgrade `TargetFramework` in **all 15** C# projects to `net10.0`; change root `global.json` to **10.0.401** with `rollForward=latestPatch`, stable/no preview, as validated in owner's installed `D:\DotNet`. Keep coherent project graph, identifiers, namespace, output shape; preserve solution build compatibility for VS 2026. Check independently omitted `tools/MigrationSmoke`.
- Move .NET-owned package family versions to compatible stable 10.x where necessary: `Microsoft.Extensions.Hosting`, `Microsoft.Extensions.Http`, `Microsoft.Extensions.Logging.Abstractions`, `Microsoft.Extensions.Configuration.Abstractions`, `Microsoft.AspNetCore.Mvc.Testing`, `Microsoft.EntityFrameworkCore`, `Microsoft.EntityFrameworkCore.Design`, `Npgsql.EntityFrameworkCore.PostgreSQL`. Stable availability must be verified; as a coherent reference baseline DocFlow uses EF Core / ASP.NET 10.0.12 and Npgsql EF provider 10.0.3, but **do not assume these necessarily resolve every TradeOps dependency**. Remove NU1605/NU190x warnings only by sound compatible change, not suppression. Keep `Swashbuckle.AspNetCore`, `MessagePack`, `Nethereum`, xunit/test tooling intact if compatible; otherwise update minimally, documenting any breaking API behavior.
- Update both API and Worker Dockerfile **SDK image 8.0 -> 10.0** and runtime image **aspnet/runtime 8.0 -> 10.0**. Respect correct ASP.NET Core vs worker image, copy/restore, output and port. If adding Docker build proof, ensure no secrets, no running a live container, and no Actions. Review deploy compose config for hardcoded net8 paths and update only actually relevant references; no testnet/live demo start.
- Update active user-facing readme/development docs with correct CLI/IDE prerequisites, selected SDK, owned Docker TFM if stale. Do not edit historical evidence as if earlier net8 CI ran at new SHA, archived documentation or unrelated marketing text. Explicitly inventory remaining legacy 8.0 references and label historical/intentional ones.

**B — Preserve security, trading and data contracts**
- No intentional changes to: risk acceptance thresholds, order execution status transitions/retries, cancellation/idempotency, exchange provider allowlist, TCP/HTTP broker path, HMAC verification/timestamp replay, TradingView gateways, operator authentication, reconcile, time zones, JSON serialization, DocFlow port and source identity, package dependencies, EF models/migration files, DB schema, seed or account config.
- Where EF Core10 / Npgsql10 or runtime changed behavior, add narrowly scoped regression coverage and minimal justified compatibility patch. A dependency upgrade must not accidentally execute existing DB startup path; tests should construct isolated fake/TestServer without real migrations or write access.
- Provider-specific smoke CLI tools are to be **compiled but NEVER RUN** when they could contact Bybit, IBKR, Deribit, etc. Mock-only mode must be explicitly proven safe before execution. No credentials in logs.

**C — Owner-local reproducibility**
- Create `scripts/to-v2-02-validate-local.ps1` starting with `Set-Location "D:\Projects\TradeOps-v2"`, using `D:\DotNet\dotnet.exe`, `DOTNET_ROOT` and `PATH`, checking branch+fetched remote HEAD, clean tracked working copy, selected SDK 10.0.401 compatible latest patch and exactly all **15** net10 projects. Use correct PowerShell `$LASTEXITCODE` guards and `throw` on any failure; do not print success after a failure or destructively reset local changes. Local `git fetch` is permitted.
- Validate `dotnet restore TradeOps.sln -warnaserror`, `dotnet build TradeOps.sln -c Release --no-restore -warnaserror`, and all **15** project files independently (including excluded MigrationSmoke) with safe restore/build, no CI. Before `dotnet test`, inspect tests for side effects and network calls; use explicit safe test categories/filters or mocks when possible. Report genuinely executed total test count, fails/skips, and build warnings, distinguishing `NOT RUN` ones. If Docker installed and suitable, optional safe `docker build` for images using no credentials; do not start containers.
- TradeOps-v2 contains a broad unit test suite; ensure xunit/test runner net10 compatibility. Include mock-safe API request signing/replay checks, risk gates, persistence mapping in an isolated fake DB if available, idempotency, adapter errors and DocFlow HTTP contract only with local fake. Do not run live vendor smoke executables.
- Cross-repo net10 compatibility: refer to merged DocFlow-v2 `main=3f09e91...` and verify boundary **source/contract compatibility only**. No cross-repo NuGet authenticated locked restore yet: packages are **unpublished/ACL unverified**. This should be separately marked **NOT RUN / BLOCKED**, not an automatic failure of pure TradeOps target migration.
- If your worker execution environment has no checkout or .NET SDK, you may edit GitHub source but must label builds/tests `NOT RUN — OWNER WINDOWS VALIDATION REQUIRED`; never invent green checks. Provide one owner-local script and exact branch/head commands. After final code HEAD, OWNER performs local full run and supplies output to orchestrator. If tests reveal failures, keep PR Draft and fix in this same workstream.

### Deliverables and acceptance

1. Source/config only in existing branch; current TFM `net10.0` for all 15 csproj; root global.json 10.0.401; dependency graph resolvable; both Dockerfiles on .NET10; no workflow edit or new vendor/network behavior.
2. New `docs/orchestration/workstreams/TO_V2_02_STATUS.md`: live SHAs/compare, actual 15-project version matrix, diff inventory, true local test evidence (or NOT RUN), image/compose exceptions, migration risks.
3. New `docs/orchestration/workstreams/TO_V2_02_MIGRATION_NOTES.md`: EF/Npgsql/ASP.NET10 breaking change analysis, security/execution/data invariants, Docker and local owner commands, blockers for unpublished DocFlow packages.
4. New `scripts/to-v2-02-validate-local.ps1`: non-invasive reproducible Windows verifier for `D:\Projects\TradeOps-v2`, no hosted workflows or production egress.
5. Update **existing Draft PR** (created by orchestrator) with final exact HEAD, files/compare and honest validation state. Do not merge. Merge and registry status handled by Development Orchestrator after owner local acceptance and independent code review.

**GitHub Actions OFF, KEEP OFF. NuGet PUBLISH BLOCKED. No real exchange orders or DB migration.**
