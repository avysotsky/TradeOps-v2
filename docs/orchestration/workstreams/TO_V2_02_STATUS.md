# TO-V2-02 — Worker Status / Handoff

**IMPLEMENTED, OWNER VALIDATION REQUIRED, DRAFT PR #4 — DO NOT MERGE.**
Recorded 2026-10-10 (Europe/Kyiv). GitHub is source of truth.

## Live repository state at finalization
- TradeOps-v2 PRIVATE main `7d6aa8b74d7e08fd0c4a7289e56948f5867a7287`; initial worker HEAD `f71f30dbe6f649aee3df660415863a48ff9693ad`.
- DocFlow-v2 PRIVATE main `3f09e91ba356fb367248ef1b48a56b19d9fdae97`, source-only cross-repo reference, unmodified.
- Assigned branch `TradeOps/to-v2-02-dotnet10-migration`; PR #4 open Draft.
- Compare vs TradeOps main is diverged: main carries one orchestrator-only registration commit outside this branch. One commit behind; do not rewrite history or merge.
- Branch HEAD: read the exact live SHA via `git rev-parse HEAD` or PR #4; update PR comment with final exact HEAD. Do not take this document's writing commit as proof of all subsequent writes.

## 15-project migration matrix

| Project | TFM | NuGet changed | In TradeOps.sln |
|---|---|---|---|
| src/TradeOps.Api/TradeOps.Api.csproj | net10.0 | Swashbuckle 10.2.3 retained | yes |
| src/TradeOps.Application/TradeOps.Application.csproj | net10.0 | Microsoft.Extensions.Logging.Abstractions 10.0.12 | yes |
| src/TradeOps.Domain/TradeOps.Domain.csproj | net10.0 | none | yes |
| src/TradeOps.Infrastructure/TradeOps.Infrastructure.csproj | net10.0 | EF Core/Design 10.0.12, Config.Abstractions/Http 10.0.12, Npgsql EF 10.0.3; MessagePack/Nethereum unchanged | yes |
| src/TradeOps.Worker/TradeOps.Worker.csproj | net10.0 | Microsoft.Extensions.Hosting/Http 10.0.12 | yes |
| tests/TradeOps.UnitTests/TradeOps.UnitTests.csproj | net10.0 | Microsoft.AspNetCore.Mvc.Testing 10.0.12; xunit/test SDK retained | yes |
| tools/TradeOps.BybitSmoke/TradeOps.BybitSmoke.csproj | net10.0 | Extensions Http/Logging 10.0.12 | yes |
| tools/TradeOps.SignedWebhookDemo/TradeOps.SignedWebhookDemo.csproj | net10.0 | none | yes |
| tools/TradeOps.TradingViewDemo/TradeOps.TradingViewDemo.csproj | net10.0 | none | yes |
| tools/TradeOps.PilotEvidence/TradeOps.PilotEvidence.csproj | net10.0 | none | yes |
| tools/TradeOps.IbkrPaperSmoke/TradeOps.IbkrPaperSmoke.csproj | net10.0 | Extensions Http/Logging 10.0.12 | yes |
| tools/TradeOps.PublicResearchDemo/TradeOps.PublicResearchDemo.csproj | net10.0 | none | yes |
| tools/TradeOps.TranscriptResearchDemo/TradeOps.TranscriptResearchDemo.csproj | net10.0 | none | yes |
| tools/TradeOps.ProviderTranscriptResearchDemo/TradeOps.ProviderTranscriptResearchDemo.csproj | net10.0 | none | yes |
| tools/MigrationSmoke/TradeOps.MigrationSmoke.csproj | net10.0 | none direct | **NO — separately validated** |

## Change inventory
- 15 csproj TFM and compatible Microsoft package major versions; global.json SDK 10.0.401.
- API/Worker Docker SDK+runtime 10.0. Deployment compose build-context paths remain valid, no compose edits needed.
- Added `scripts/to-v2-02-validate-local.ps1` and this status and migration notes.
- No production C# source, EF migrations/models, persistence schema, security thresholds, order state machine, public DTOs, DocFlow adapter, .github/workflows or other repo modified.

## Evidence
- GitHub source read and file writes: performed with connected private GitHub repository.
- SDK version command: **NOT RUN — owner Windows required**.
- dotnet restore/build 14-project solution and standalone 15 projects: **NOT RUN**.
- xUnit risk, order, webhook signing, replay, HTTP DocFlow adapter, integrations: **NOT RUN**.
- Docker build: **NOT RUN**; containers not started.
- PostgreSQL isolated database tests: **NOT RUN**, excluded from unattended filter.
- Cross-repo DocFlow private package restore/consumer: **NOT RUN / BLOCKED** (package publication and ACL approval).
- GitHub Actions: OFF; no new runs requested, no workflow modifications.
- Build warnings, test counts, failure and skip counts: **UNKNOWN — NOT RUN**, no claims of green CI.

## Owner action / orchestrator handoff

Owner: check out same branch in `D:\Projects\TradeOps-v2` with SDK `D:\DotNet\dotnet.exe`, then run `& .\scripts\to-v2-02-validate-local.ps1` and capture full logs with exact SHA, warning totals and test counts. Do not run smoke tools, compose up, API/Worker, or production DB migrations. TestServer/DB integration suites require a separate safety review and disposable DB.
Orchestrator: independently verify final head, per-file compare, branch divergence, new-head owner evidence, no live CI run, contract and privacy invariants. Keep PR Draft pending acceptance. No merge.

## Owner NU1903 failure and corrective patch — 2026-10-10

Owner validation at exact prior HEAD `32fa8a2c3bac6c9508b8ab7d378d31b2ea0a5bf5` FAILED during `dotnet restore TradeOps.sln -warnaserror`: **five NU1903 errors** for resolved Newtonsoft.Json 11.0.2 (GHSA-5crp-9r3c-p9vr, CVE-2024-21907). Subsequent builds and tests were **NOT RUN**, rather than passed. Orchestrator comment in Draft PR #4 documents the failure.

The published package metadata for direct dependency `Nethereum.Signer.EIP712 6.1.0` declares `Newtonsoft.Json >=11.0.2 && <14.0.0`, making it a strongly documented source. The *exact owner's assets graph* was not accessible in this worker environment, so it has **NOT** been independently inspected. Added a direct `Newtonsoft.Json` **13.0.4** reference only to TradeOps.Infrastructure to override the vulnerable lower bound without modifying Nethereum/EIP-712 implementation. Updated PowerShell validator to reject missing assets or any resolved Newtonsoft.Json version other than 13.0.4 in every project's `project.assets.json`, with an explicit Infrastructure assertion. `-warnaserror` and default NuGet Audit remain enabled; no warning suppression. EIP-712 signature/serialization regression remains **NOT RUN** and requires owner follow-up. No trading, APIs, EF migrations, or production code changed.

**New-HEAD validation:** NOT RUN in worker environment. Owner must rerun full validator on current PR HEAD; compare `dotnet list` dependency resolution and test results. PR remains Draft and merge blocked.
