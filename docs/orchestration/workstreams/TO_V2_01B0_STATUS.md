# TO-V2-01B0 — In-Process Adapter Design / Worker Handoff

**Status:** DESIGN COMPLETE / READY FOR ORCHESTRATOR REVIEW / IMPLEMENTATION BLOCKED / DO NOT MERGE.  
**Date:** 2026-10-09 (Europe/Kyiv). **Repository:** PRIVATE `avysotsky/TradeOps-v2`.  
**Existing branch:** `TradeOps/to-v2-01b0-inprocess-adapter-contract`.  
**Draft PR:** [#3](https://github.com/avysotsky/TradeOps-v2/pull/3).  
**Original registered branch HEAD:** `7666e2d878f23fed900b22d498f62f3d5a35539c`.  
**Initial branch baseline / merge base at preflight:** `8f1963e1dad4d78fc81b9985e8b0f00531c9814c`.  
**Last confirmed design-document commit prior to this status file:** `0d2473802bf743d610ef5329385a44ad5b63ae6b`.  
**Final branch HEAD:** must be read back from GitHub **after** committing this self-referential status file and recorded in the existing PR #3 final handoff comment; do not confuse the design commit with the completed branch HEAD.

## Live source-of-truth preflight

| Item | Verified state (2026-10-09) |
|---|---|
| TradeOps-v2 main | `2a2558e6e9b49cbc539b8b85bc59c82271fbcea7`; registry-only last main commit. Exact-main build Actions [37942188664](https://github.com/avysotsky/TradeOps-v2/actions/runs/37942188664) `SUCCESS`. |
| This assigned branch at start | `7666e2d878f23fed900b22d498f62f3d5a35539c`; preflight compare to live main: `diverged`, 1 ahead / 1 behind; only `TO_V2_01B0_ASSIGNMENT.md` changed relative to main. Existing branch's build [37942133483](https://github.com/avysotsky/TradeOps-v2/actions/runs/37942133483) `SUCCESS` **at registered HEAD**, not evidence for later design commits. |
| DocFlow-v2 main | `0f009e79126a9beee5eef4b6eecbef1725937e12`, already integrates native application facade and secure extraction HTTP. Prior exact-main native HTTP [37940276591](https://github.com/avysotsky/DocFlow-v2/actions/runs/37940276591) and .NET [37940276566](https://github.com/avysotsky/DocFlow-v2/actions/runs/37940276566) `SUCCESS`. |
| Concurrent DF-V2-04 | DocFlow branch `DocFlow/df-v2-04-private-dotnet-packaging`, HEAD `7c68bdbbecf07a0c160874e36dfe6adce1a13ff1`, [Draft PR #10](https://github.com/avysotsky/DocFlow-v2/pull/10) open/unmerged. Compare 5 ahead / 0 behind, 11 owned files. Exact-HEAD packaging [37943102131](https://github.com/avysotsky/DocFlow-v2/actions/runs/37943102131) `SUCCESS`, as are six other exact-HEAD checks: `37943102012`, `37943102055`, `37943102128`, `37943102137`, `37943102172`, `37943102179`. **PUBLISH BLOCKED** despite local-feed pass. |
| Parallel PRs | TradeOps-v2: only open Draft PR #3; DocFlow-v2: only open Draft PR #10 (at preflight). Both repositories PRIVATE. Public v1 archived repositories untouched. |

**Overlap assessment:** this worker edits *only* TradeOps-owned TO-V2-01B0 documentation in a distinct repository from DF-V2-04's DocFlow package work. The main-side concurrent TradeOps change was in the orchestrator-owned `V2_WORKSTREAM_REGISTRY.md`, which this worker did not alter. Orchestrator must recompute live compare and CI before merging.

## Delivered documentation

1. `docs/orchestration/workstreams/TO_V2_01B0_INPROCESS_ADAPTER_DESIGN.md` — implementation-ready RFC for the *existing* TradeOps port → a future in-process adapter → DocFlow-owned native facade; explicit field-by-field mapping, JSON cloning/ownership, structural results and IDs, DI and exclusive topology, no internal HTTP/Python, scoped lifetimes and fresh backends, cancellation/timeouts, quota/transport compatibility, sanitized failure categories, dependency/package gates and G3 fixture matrix; G4/TO-V2-01D sequencing and zero broker/database mutation.
2. **This status file**, `docs/orchestration/workstreams/TO_V2_01B0_STATUS.md` — evidence, unresolved risks, downstream ownership.
3. Existing registered `TO_V2_01B0_ASSIGNMENT.md` preserved exactly; no assignment edits necessary.

**Not delivered by design:** any C# implementation, runtime/host wiring, NuGet config, test fixture, actual CI workflow, package publish/restore, schema changes, provider SDK, real model/broker calls, production DB migrations or integration merge. New design is reviewed source + **proposed future assertions**, not a claim of passing G3 runtime tests.

## Reviewed TradeOps source

- `src/TradeOps.Application/Models/DocFlowExtractionContracts.cs` — exact immutable two-JsonElement request and two-JSON-string result, `IDocFlowExtractionPort` signature.
- `src/TradeOps.Infrastructure/DocFlow/DocFlowHttpExtractionAdapter.cs` — version 1 envelope, 1,048,576-byte request / 2,097,152-byte response limits; recognized openai/groq; existing generic non-2xx exception.
- `src/TradeOps.Infrastructure/DocFlow/DocFlowServiceCollectionExtensions.cs` — opt-in typed `HttpClient`, 30-second timeout, redirects disabled.
- `src/TradeOps.Infrastructure/TradeOps.Infrastructure.csproj` — current net8.0, no native DocFlow package references.
- `src/TradeOps.Api/Program.cs` — current DI/host and **startup PostgreSQL `Database.MigrateAsync()`**. This cannot be used unmodified for zero-DB-mutation parity tests.
- `docs/orchestration/workstreams/TO_V2_01A_TOPOLOGY_DESIGN.md`, `docs/orchestration/DUAL_TOPOLOGY_ARCHITECTURE_PLAN.md`, `docs/orchestration/WORKSTREAM_PROTOCOL.md` and `docs/orchestration/V2_WORKSTREAM_REGISTRY.md` — accepted direction, workstream ownership, frozen boundaries, existing risk/dry-run non-mutation gates.

## Reviewed DocFlow-v2 source and package draft

- `src/DocFlow.Extraction.Application/{IDocFlowExtractionFacade,DocFlowExtractionFacade,ExtractionRequests,ExtractionArtifacts,IExtractionBackendFactory}.cs` — explicit raw/normalized operations, fresh backend factory, owned cloned JsonElements; facade has no default production backend or explicit factory-backend disposal.
- `src/DocFlow.SchemaExtraction/{SchemaExtractionRequest,NativeExtractionService,IExtractionBackend,IRequestAwareExtractionBackend,IConfidentExtractionBackend,ExtractionResultFactory,ValidationOutcome}.cs` — strict schema envelope / subset, backend confidence, exact engine/data/validation diagnostic result shapes.
- `src/DocFlow.Normalization/{TextDocumentNormalizer,TextDocumentNormalizer.Normalized}.cs` — source identity, canonical SHA-256, ID/fingerprint integrity, raw vs already-normalized boundary.
- `src/DocFlow.Api/Extraction/{ExtractionHttpOptions,ExtractionHttpEndpoint,ExtractionRequestParser}.cs` — opt-in service JWT/scope and HTTPS, authorized provider selection, deadlines/bulkhead, transport UTF8/tree/size/duplicate constraints, sanitized wire failure codes.
- `docs/workstreams/{DF_V2_02E_STATUS,DF_V2_03_STATUS}.md`, DF-V2-04 branch `docs/workstreams/DF_V2_04_{ASSIGNMENT,STATUS}.md` and `docs/packaging/PRIVATE_NUGET_RELEASE_POLICY.md` — verified merged-vs-proposed state and release blockers.

## Explicit unresolved gates / decisions

1. **G5 package release not achieved:** DF-V2-04 candidate IDs `DocFlow.Normalization`, `DocFlow.SchemaExtraction`, `DocFlow.Extraction.Application` at `2.0.0-preview.1` / net8.0 have locally passed packaging CI. They are **not published**; registry name/version collision, proprietary license/security, publisher rights, effective private package visibility, TradeOps-v2 package-specific read-only ACL, authenticated cross-repository `--locked-mode` restore and immutable released hashes are unknown/unapproved. Do not start production TO-V2-01B until separately cleared.
2. **Exact failure-code parity gap:** existing HTTP adapter masks non-2xx wire codes; native facade currently uses broad exceptions and HTTP parser partly infers unsupported schema errors from messages. A stable sanitized error taxonomy or approved DocFlow-owned typed-error contract is necessary before claiming negative HTTP/in-process parity.
3. **Ingress parity gap:** DocFlow.Api transport parser's recursive duplicate detection, 1 MiB/256 KiB/12k-node/depth32/segments1024/participants512 guards are not all in facade. Future adapter must enforce equivalent transport shape/limits or consume a new approved shared non-HTTP policy; cannot duplicate native schema validation.
4. **Backend resource lifetime:** DocFlow facade creates a fresh backend, but does not currently dispose an `IDisposable` or `IAsyncDisposable` backend; define production factory/operation scope and prove no resource leak. Never reuse mutable confidence-bearing backend across concurrent calls.
5. **Provider and host security:** no real provider factory is shipped in packaged candidate; allowed selection, outbound egress/credentials, CI-only offline backend, DocFlow whole-host legacy unauthenticated `DocumentsController`, HTTPS/OIDC audience/scope/service identity and deployment restrictions require separate owner approval.
6. **G4 zero persistence:** TradeOps.Api startup runs `MigrateAsync`. A monolith composition/test-only root must prevent actual DB writes/migrations and real broker operations; TO-V2-01C owns changes required for this.
7. **Parity and trading gates:** future G3/G4/G5 and TO-V2-01D require fixed backend/snapshot fixtures, two topology comparison, risk preview/dry-run non-mutation, exact-head checks. No approved live execution from this workstream.

## Next workstream ownership and handoff

- **Development Orchestrator:** verify PR #3 exact final HEAD, check changed files strictly `TO_V2_01B0_ASSIGNMENT.md` (preexisting), `TO_V2_01B0_INPROCESS_ADAPTER_DESIGN.md` and `TO_V2_01B0_STATUS.md` (added), review RFC decisions; alone decides merge/branch synchronization, package release and worker launch.
- **DF-V2-04 / package owner + orchestrator:** finish separate publish/licensing/privacy/immutable registry and authenticated **read-only TradeOps consumer ACL** gates; existing PR #10 remains Draft and must not be altered by this worker.
- **TO-V2-01B:** only after G5, implement direct port adapter, package-only DI, tests/quota/error model and G3 proof, without touching TradeOps.Application/Domain; ask orchestrator for approval if DocFlow typed error or disposal contract changes are required.
- **TO-V2-01C:** single PID modular host and safe composition, no loopback HTTP/migration/broker side effects.
- **TO-V2-01D:** deterministic native HTTP/InProcess full downstream `ResearchDecision → RebalancePlan → risk preview → execution dry-run` parity and zero-mutation checks.

**Stop condition:** documentation-only branch delivered to existing Draft PR #3; final HEAD and CI/compare measured **after status commit** and recorded in final PR comment. **No merge, no production edits, no package publication, no changes to DocFlow-v2 or archived v1.**
