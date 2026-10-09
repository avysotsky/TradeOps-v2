# TO-V2-01B0 — In-Process DocFlow Adapter Contract & Integration Design

**Status: REGISTERED / NOT STARTED — independent documentation-only worker.**

## Registration / no duplicate branch

- Private repository: \`avysotsky/TradeOps-v2\`.
- **Existing worker branch:** \`TradeOps/to-v2-01b0-inprocess-adapter-contract\`. Do not create another.
- Branch initial base: TradeOps-v2 \`main=8f1963e1dad4d78fc81b9985e8b0f00531c9814c\` (build [37940719498](https://github.com/avysotsky/TradeOps-v2/actions/runs/37940719498) SUCCESS).
- DocFlow-v2 reference \`main=0f009e79126a9beee5eef4b6eecbef1725937e12\`, DF-V2-03 merged PR #9 with exact-main secure HTTP CI [37940276591](https://github.com/avysotsky/DocFlow-v2/actions/runs/37940276591) SUCCESS and .NET CI [37940276566](https://github.com/avysotsky/DocFlow-v2/actions/runs/37940276566) SUCCESS.
- **Concurrent DF-V2-04** in DocFlow-v2: \`DocFlow/df-v2-04-private-dotnet-packaging\`, draft [PR #10](https://github.com/avysotsky/DocFlow-v2/pull/10); registered assignment-only head \`4e95301ab07ee1cc6d5cd96d4933271de538394f\`. The package IDs/dependency versions/consumer ACL are **not accepted or released**. Recheck live state before work.
- Public v1 \`avysotsky/TradeOps\` and \`avysotsky/DocFlow\` remain PUBLIC+ARCHIVED; do not touch.

## Independent workstream boundary

**You are a design/contract review worker, not TO-V2-01B production implementer.** This work may run in parallel with DF-V2-04 because it edits only *TradeOps-owned* documentation; DF-V2-04 edits DocFlow packaging metadata, tests and CI. No shared files.

Aim: deliver a **specific, implementation-ready, DocFlow-native in-process adapter contract RFC**, permitting the future TO-V2-01B worker to implement the existing TradeOps-owned \`IDocFlowExtractionPort\` using an injected \`IDocFlowExtractionFacade\` in the **same .NET process**, without localhost HTTP, extra Python interpreter, duplicated normalization/schema validation or changes to TradeOps.Application/Domain.

The only approved production integration technique for eventual TO-V2-01B is a **versioned, private, pinned package** for native DocFlow assemblies after DF-V2-04 and authenticated package-read permission for TradeOps-v2. A developer-only cross-repo \`ProjectReference\` prototype is an earlier design option, NOT a release dependency; do not require it to finish this docs-only RFC. If DF-V2-04 changes package design while you work, report dependency drift; do not modify its PR.

## Mandatory preflight

Read live GitHub state for both private \`main\` branches; both open PR lists and branch heads; compare your assigned branch with current TradeOps \`main\`; exact CI, active workstreams and overlap. Then read:

**TradeOps-v2 (read all relevant source, but do not modify):**
- \`docs/orchestration/workstreams/TO_V2_01A_TOPOLOGY_DESIGN.md\` — accepted dual-topology ADR, G3/G4/G5, ownership and security gates.
- \`docs/orchestration/WORKSTREAM_PROTOCOL.md\`, \`docs/orchestration/V2_WORKSTREAM_REGISTRY.md\`, \`docs/orchestration/DUAL_TOPOLOGY_ARCHITECTURE_PLAN.md\`.
- \`src/TradeOps.Application/Models/DocFlowExtractionContracts.cs\` — port \`IDocFlowExtractionPort\`, request with raw JsonElement/schema/provider/model/name, output JSON strings.
- \`src/TradeOps.Infrastructure/DocFlow/DocFlowHttpExtractionAdapter.cs\`, \`DocFlowServiceCollectionExtensions.cs\`, \`TradeOps.Infrastructure.csproj\`.
- \`src/TradeOps.Api/Program.cs\` plus relevant research/preview/risk/dry-run composition; current tests and build CI. Distinguish existing v1 Python embedding feasibility docs from the new **native .NET shared facade**; never design Python embedding as the primary native path.

**DocFlow-v2 (read-only):**
- \`src/DocFlow.Extraction.Application/{IDocFlowExtractionFacade,DocFlowExtractionFacade,ExtractionRequests,ExtractionArtifacts,IExtractionBackendFactory}.cs\`;
- \`src/DocFlow.SchemaExtraction/{SchemaExtractionRequest,NativeExtractionService,IExtractionBackend,IRequestAwareExtractionBackend,IConfidentExtractionBackend}.cs\`; \`src/DocFlow.Normalization/TextDocumentNormalizer.Normalized.cs\`;
- \`src/DocFlow.Api/Extraction/{ExtractionHttpOptions,ExtractionHttpEndpoint,ExtractionRequestParser}.cs\`;
- \`docs/workstreams/DF_V2_02E_STATUS.md\`, \`DF_V2_03_STATUS.md\` and DF-V2-04's live \`docs/workstreams/DF_V2_04_ASSIGNMENT.md\` on its branch (NOT on main until merged).

If documentation is outdated, clearly separate observed live source from historical notes. Do not make unsupported promises about full JSON Schema Draft 2020-12, provider availability or authenticated package access.

## Deliverable: TO-V2-01B0 contract and implementation specification

Write a detailed \`docs/orchestration/workstreams/TO_V2_01B0_INPROCESS_ADAPTER_DESIGN.md\` that covers all of the following:

1. **Exact v1 request/response mapping.** Table each \`TradeOps.Application.Models.DocFlowExtractionRequest\` field to \`RawExtractionRequest\`, \`SchemaExtractionRequest.Parse\`, \`BackendSelection\`, \`DocumentName\` and the two JSON strings in \`DocFlowExtractionResult\`. Demonstrate \`JsonElement\` ownership/cloning; preserve numeric semantics, source/participant/segment IDs, canonical SHA-256 fingerprint, engine, \`validation_status\`, data and error diagnostics. Do not synthesize or overwrite identity. Explain the transport-only \`schemaVersion:1\` vs schema metadata \`schema_version\` distinction.
2. **Explicit DI topology.** Provide diagrams and pseudocode for \`IDocFlowExtractionPort\` → TradeOps Infrastructure in-process adapter → DocFlow \`IDocFlowExtractionFacade\` → \`IExtractionBackendFactory\`. \`TradeOps.Application\`/Domain keep no DocFlow package reference; no changes to existing public port. Design opt-in, exclusive HTTP **or** InProcess registration (never two port bindings), required startup guards and no silent fallback.
3. **No hidden network topology.** No \`HttpClient\`/loopback socket/Python subprocess in InProcess adapter. Legitimate provider egress inside DocFlow's provider adapter is a separate, explicit policy; deterministic fake provider in tests performs **zero external I/O**. Propose an injected spy/failing \`HttpMessageHandler\` and counting factory plus assertions that internal HTTP calls and broker/persistence mutations equal zero.
4. **Failure model.** Typed failure mapping from DocFlow invalid raw/request/schema/unsupported Draft feature/unknown provider/factory unavailable/invalid result/cancellation/timeout/transient backend failure to TradeOps-owned sanitized error categories, retaining current port compatibility. Never mark \`invalid\` as \`valid\` and never treat a green risk *preview* as live execution approval. Distinguish cancellation from timeouts, provider faults from validation; do not add a second schema validator.
5. **Lifetime and concurrency.** Factory produces fresh backend per operation, facade/service container singleton/scoped/transient analysis, confidence state isolation, cancellation token propagation, no mutable per-call shared state; disposal and exception boundaries. Monolith extraction does not depend on DocFlow DB/storage application modules, tenant data or \`DocFlow.Api\`.
6. **Private packages readiness.** Specify NuGet package ID/version/dependency decision contingent on **actual DF-V2-04 outcome**, locking/restore plan for a separate private TradeOps CI, GitHub Packages read ACL, supply-chain/license gates, Windows/Linux/Docker constraints. Do not invent published package versions or permissions. Document local ProjectReference only as an explicit non-release prototype option.
7. **Deterministic G3 acceptance matrix.** Concrete fixture(s) and negative tests: schema valid/invalid, unsupported keyword, tampered identity, malformed envelope, forbidden provider/model, cancellation/timeout, confidence null/bounds, high concurrency/selector isolation and no provider/backend duplicate invocation; exact response fields and comparison with existing HTTP adapter within supported subset; zero internal HTTP, broker orders or DB persistence mutations. Define unambiguous assertions/evidence needed from future TO-V2-01B worker.
8. **G4 sequencing.** Next implementer B only after E facade+DF-V2-04 verified package and package consumer ACL; TO-V2-01C owns single-PID modular monolith composition; TO-V2-01D owns HTTP-vs-InProcess parity including TradeOps ResearchDecision → RebalancePlan → risk preview → execution dry-run, with fake broker and persistence; no live trading.
9. **Security gates.** A package can be private while \`DocFlow.Api\` remains unfit for public deployment: legacy \`DocumentsController\` unauthenticated and extraction JWT service-identity policy needs production ACL. Do not conflate package security with HTTP host exposure.

Create also \`docs/orchestration/workstreams/TO_V2_01B0_STATUS.md\` with exact SHA evidence, reviewed source names, unresolved decisions and next worker ownership.

## Ownership / prohibited operations

**Allowed:**
- \`docs/orchestration/workstreams/TO_V2_01B0_ASSIGNMENT.md\` (this task);
- \`docs/orchestration/workstreams/TO_V2_01B0_INPROCESS_ADAPTER_DESIGN.md\`;
- \`docs/orchestration/workstreams/TO_V2_01B0_STATUS.md\`.

**Forbidden:**
- Any production C#, tests, solution, project, NuGet config, workflow or deployment changes; any DocFlow-v2 change or package publish.
- Edits to shared \`docs/orchestration/V2_WORKSTREAM_REGISTRY.md\` (orchestrator owns it), \`TO_V2_01A_TOPOLOGY_DESIGN.md\`, \`WORKSTREAM_PROTOCOL.md\`, API/port contract, HTTP adapter, archived v1.
- Creating additional branches or PRs, merging PRs, changing repository visibility or enabling live broker/exchange/model calls.

## Review / completion gate

Hand back an independently reviewable design-only Draft PR with complete RFC and status, explicit unresolved package+provider+security constraints, exact final branch HEAD, file diff with **only** the three designated markdown files, current compare, any relevant CI status and a final PR comment. GitHub Actions on docs-only branch **may not trigger** due to path filters; do not invent an exact-head green code run. Evidence means reviewed code and accurate test plans, not implementation. Stop after delivery; only Development Orchestrator reviews and merges the RFC.

**No production change, no package publication, no worker-initiated merge.**
