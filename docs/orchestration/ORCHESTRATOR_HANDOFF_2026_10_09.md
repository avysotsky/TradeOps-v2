# Development Orchestrator — Handoff to next chat

**Snapshot:** 2026-10-09, Kyiv time. **Source of truth: live GitHub**, not this static snapshot. This note transfers **orchestration**, not implementation. Check all current heads, branch compares, PRs and exact-head Actions before any change.

## Non-negotiable role separation

- This chat is **Development Orchestrator**. Plan architecture, create/assign dedicated branches and full worker tasks, review independent work, exact-head CI, audit/security and scope, sequence/merge accepted PRs, maintain this registry.
- **Never implement worker production code or test logic inside the orchestrator chat.** Work is done in separate worker chats launched by the user. A registered PR/branch does not mean a worker has started. Do not merge on behalf of a worker until independently reviewed.
- Workers must not create redundant branches, edit overlapping files, move unrelated source, merge their own PRs or change public v1 repositories.
- Security/operational rules: both v2 repos **private**; both original v1 repos **public+archived**, links preserved. No API keys/secrets committed or logged, no live broker trading, no unapproved provider calls, no public unauthenticated HTTP endpoint.
- No future-work promises in chat: complete orchestration tasks in the current turn or explicitly identify external worker dependency.

## Four-repository state, last live verification

| Repo | Visibility / status | main SHA | Main relevant CI |
| --- | --- | --- | --- |
| `avysotsky/TradeOps-v2` | PRIVATE, active | `06821b7c522ee575579f30aec6b54054d945acb1` | build `37929538543` **SUCCESS** |
| `avysotsky/DocFlow-v2` | PRIVATE, active | `39d01c71ad53059e00b64d66508038371b9435e2` | latest code HEAD `da1081586c5176d3c8ace65175b7329c5df7d4ae`: native/Python differential `37929129214` **SUCCESS**, Python `37929129026` **SUCCESS**, .NET `37929129060` **SUCCESS** |
| `avysotsky/TradeOps` | PUBLIC + ARCHIVED | `73fcd9c6fd825d352ce4550d917e026f73199109` | frozen legacy portfolio |
| `avysotsky/DocFlow` | PUBLIC + ARCHIVED | `f8f0e05b7e923024c350b961e48ed2040f752400` | frozen legacy portfolio |

**Important:** final DocFlow-v2 main is a docs-only architecture merge atop the above tested code SHA; do not interpret missing CI on docs-only main as full runtime retest, or imply DocFlow production HTTP extraction is ready.

## Architecture / product objective

Ship **two independently deployable ASP.NET Core HTTP services**, `TradeOps.Api` and `DocFlow.Api`, **and** enable the same native application/business logic to compose in one .NET modular monolith through DI with **no localhost HTTP**. TradeOps.Application keeps its existing `IDocFlowExtractionPort`; distributed binding is HTTP adapter; monolith binding is in-process adapter. DocFlow owns normalization/extraction/schema validation/providers; TradeOps domain/app must not depend on concrete DocFlow types. Same deterministic fixture must yield equivalent normalized/structured artifacts and downstream research/risk/dry-run results. Absolutely no broker mutations from parity tests.

Accepted planning: [TO-V2-01A](https://github.com/avysotsky/TradeOps-v2/blob/main/docs/orchestration/workstreams/TO_V2_01A_TOPOLOGY_DESIGN.md). Proposed initial integration with local cross-repo ProjectReference, then immutable **private NuGet** packages after parity and license/ACL review, pinned on net8.0 pending separately planned framework upgrade. No third composition repo needed; TradeOps-v2 owns future host. User specifically wants C#-first business code.

## Completed/merged private DocFlow workstreams

| ID | Integrated proof | Scope / caveat |
| --- | --- | --- |
| DF-V2-01 | PR #2, 58 normalization parity fixtures | strict normalization/identity; not extraction parity |
| DF-V2-02A | PR #3, native restricted schema subset | not full JSON Schema Draft 2020-12 |
| DF-V2-02B | PR #4, native/Python synthetic extraction differential | initially 36 cases |
| DF-V2-02C | PR #5, independent audit | original audit had **3 BLOCKER, 10 MUST_FIX, 4 FOLLOW_UP** at historical DF-02A baseline; disposition per D handoff |
| DF-V2-02D | [PR #6](https://github.com/avysotsky/DocFlow-v2/pull/6), merged `da1081586c5176d3c8ace65175b7329c5df7d4ae` | canonical normalized-document ingress without re-normalization, strict schema metadata, optional backend-aware request, cancellation and CI. Main CI `37929129214` **58/58 differential + 46 native assertions**, `37929129026` **120 passed / 116 skipped**, `37929129060` .NET **SUCCESS** |
| DF-V2-02E0 | [PR #7](https://github.com/avysotsky/DocFlow-v2/pull/7), merged `39d01c71ad53059e00b64d66508038371b9435e2` | accepted facade architecture RFC direction, **no runtime implementation**. Read `docs/workstreams/DF_V2_02E0_FACADE_CONTRACT_DESIGN.md` |

Legacy v1 archives are unaffected by private v2 work.

## Active next workstream: DF-V2-02E

- Repo: `avysotsky/DocFlow-v2` (PRIVATE).
- Existing worker branch: `DocFlow/df-v2-02e-extraction-facade-implementation`.
- Registered branch HEAD: `563761f14bda62ee2ac15a7696c1b29fcb6e1d1d`.
- Base DocFlow-v2 main: `39d01c71ad53059e00b64d66508038371b9435e2`.
- [Draft PR #8](https://github.com/avysotsky/DocFlow-v2/pull/8), currently **OPEN / DRAFT / assignment-only** at last verification. No worker implementation detected yet. The user has been given an exact startup prompt to launch a separate DF-V2-02E worker chat.
- Full task: `docs/workstreams/DF_V2_02E_ASSIGNMENT.md`.
- Owns only **new** `src/DocFlow.Extraction.Application/**`, new focused tests/CLI, new `.github/workflows/df-v2-02e-facade.yml`, worker status docs; no edits to D-owned normalization/schema core, Python production, DocFlow.Api, TradeOps or public v1.
- Needs `IDocFlowExtractionFacade` with `ExtractRawAsync` and `ExtractNormalizedAsync`, both returning normalized document + structured result, using **the same existing** D normalization and native extraction engine; injected synthetic backend provider factory, strict request/schema capability, immutable artifacts, metadata/confidence/error/cancellation/concurrency tests. Dedicated exact-head PR + main-push CI must compile and run facade (current `DocFlow.sln` does not necessarily cover it). **No HTTP host, NuGet publish, real Groq/OpenAI backend or secret use.**
- After worker finishes: inspect live PR diff, file scope, code/architecture, exact-head CI, audit gaps. If acceptable, merge as orchestrator, then inspect exact main CI. Do not assume worker completed merely because user says 'next'.

## Next dependency order (do not leapfrog)

1. **DF-V2-02E** facade implementation, independent verification/merge.
2. **DF-V2-03** authenticated ASP.NET Core `DocFlow.Api POST /api/v1/extractions`, Python v1 contract-compatible where capability permits, safe 422/413/502 classifications, versioning, auth, TLS, redaction, ingress limits, negative tests and Python regression.
3. **DF-V2-04** private immutable NuGet/native package publishing and TradeOps consumer ACL/locked restore; only after facade/security/license gate.
4. **TO-V2-01B** direct in-process adapter in TradeOps.Infrastructure binding unchanged TradeOps-owned `IDocFlowExtractionPort` to DocFlow facade, no internal HTTP.
5. **TO-V2-01C** single-process host within TradeOps-v2 with explicit topology selector, one binding/DI scope and startup safety.
6. **TO-V2-01D** HTTP-vs-in-process deterministic E2E parity and **zero broker/persistence mutations** across research/rebalance/risk/dry-run.

May run **independent audits and design docs** in parallel if file ownership does not overlap and an unmet upstream interface isn't assumed final. Register worker branch + full assignment + Draft PR from fresh live baseline, then give user exact new-chat prompt. Do not write worker code from this orchestrator.

## Remaining production blockers / decisions

- Native `SchemaSubset` is bounded; does **not** support full Python JSON Schema Draft 2020-12. Unknown keyword rejection must remain fail-closed, with explicit capability/compatibility handling.
- Full cross-runtime duplicate JSON member and arbitrary-precision numeric behavior, exhaustive schema meta-validation, depth/width/size/time/concurrency budgets, provider error redaction and real auth/egress policies remain open.
- License/Redistribution review and .NET 8 support horizon/upgrade to .NET 10 require separate gated decisions; no silent dependency or TFM upgrades.
- Existing Python FastAPI `/api/v1/extractions` is **not service-authenticated**; not suitable as publicly exposed production endpoint.
- No paper/live IBKR account is needed for architecture work; do **not** enable live order/exchange actions.

## Orchestrator reference files

- TradeOps-v2: `docs/orchestration/V2_WORKSTREAM_REGISTRY.md`, `docs/orchestration/V2_ACTIVE_ROADMAP.md`, `docs/orchestration/DUAL_TOPOLOGY_ARCHITECTURE_PLAN.md`, `docs/orchestration/WORKSTREAM_PROTOCOL.md`, `docs/orchestration/workstreams/TO_V2_01A_TOPOLOGY_DESIGN.md`.
- DocFlow-v2: `docs/audits/DF_V2_02C_NATIVE_EXTRACTION_AUDIT.md`, `docs/workstreams/DF_V2_02D_STATUS.md`, `docs/workstreams/DF_V2_02E0_FACADE_CONTRACT_DESIGN.md`, `docs/workstreams/DF_V2_02E_ASSIGNMENT.md`.
- Historic private MIG-01/02 acceptance in each v2 repo. Public v1 read-only source/snapshots remain available at original links.

## First action for successor orchestrator

1. Fetch live state **both private main commits, active PRs #8 and other workstreams, branch compares and exact-head CI**, confirm v1 public archives.
2. If DF-V2-02E is active, monitor its PR and wait for explicit completion; if not, provide user an exact startup worker prompt pointing to the existing branch and assignment (do not create another).
3. When worker returns, perform independent scope/security/compatibility/CI review, integrate only after gates, check main CI.
4. Keep workstream registry and this handoff superseded by fresh GitHub state. Continue registering separate design/audit workers if beneficial, but **never code workers' production implementation in orchestrator chat**.
