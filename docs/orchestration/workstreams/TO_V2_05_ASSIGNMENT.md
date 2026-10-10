# TO-V2-05 — Independent Offline DocFlow NuGet Consumer Contract Gate

**REGISTERED / ASSIGNMENT ONLY / WORKER NOT STARTED. Existing branch and Draft PR only.**

## Repositories and immutable registration

- Worker-owned private repo: `avysotsky/TradeOps-v2`.
- Existing branch: `TradeOps/to-v2-05-offline-docflow-package-consumer-gate`.
- Starting TradeOps main: `0b2a5216c5528cb49d41eb9f5a43870e935da3ef`, .NET 10 SDK 10.0.401; current 15 tracked .csproj inventory MUST NOT expand.
- **Read-only** DocFlow-v2 current main: `62fa3c3c77134cf923233e51fcba7c4f08eb2ef7`; approved DF-V2-07 **local candidate source SHA `927eddee08d084e7759eef1b0a67404711d2b3b1`** is DISTINCT from present DocFlow main. Never repin staged six artifacts to the current main merely because docs have since merged.
- Concurrent **DF-V2-10** works only in DocFlow-v2 Draft PR #16 on TestServer security characterization (`tests/DocFlow.Api.WholeHost.Security.Tests/`, `scripts/df-v2-10-validate-local.ps1`). Separate repo, no file overlap.
- Owner PC: Windows, TradeOps `D:\Projects\TradeOps-v2`, DocFlow `D:\Projects\DocFlow-v2`, SDK `D:\DotNet\dotnet.exe` 10.0.401, previously staged candidate example `D:\DocFlowCandidates\DF-V2-07-20261010-193712`. Stage location is operator supplied; **never assume it exists** or silently locate new archives. Do not ask for credentials.

## Motivation and strict purpose

DF-V2-07 previously verified three library **candidate** packages `DocFlow.Normalization`, `DocFlow.SchemaExtraction`, `DocFlow.Extraction.Application` version `2.0.0-preview.1` (`net10.0`), 6 retained candidate `.nupkg/.snupkg` archives plus `manifest.json` and 13/13 **generic** package-only external consumer assertions. That evidence is DocFlow-owned and does not prove that a **TradeOps-owned** .NET 10 consumer can restore/use private candidate binaries while preserving TradeOps `IDocFlowExtractionPort` boundary. NuGet packages were **NOT published**, reader ACL and legal redistribution rights remain **BLOCKED**.

Deliver an independent **TradeOps-owned, synthetic and nonpublishing** candidate-only ABI/contract probe using the exact six staged local artifacts. It must inspect the actual provenance and compile/run a **temporary sandbox test consumer** that references TradeOps.Application **source project** and the **DocFlow candidate packages** (not DocFlow project source). Prove compatible request/result mapping and package dependency closure without implementing or registering a real TradeOps production in-process adapter, without copying DocFlow library source into TradeOps, and without external providers/HTTP services/brokers.

This is an **interoperability research/test fixture**, not permission to ship native DocFlow into a TradeOps production process. Production TO-V2-01B remains blocked pending actual authorized private immutable NuGet publication, licensing, namespace, secure package reader ACL, fresh authenticated package-only restore and separate G3/G4 gates.

## Worker file ownership

1. `tools/DocFlowCandidateConsumerProbe/Program.cs` — standalone synthetic-only test source, compiled into disposable external `.csproj`. It may use *public library API* of the three package assemblies and TradeOps.Application port contract, but MUST NOT define a production adapter class for DI or change `src/`. Use fake `IExtractionBackendFactory`, no real provider.
2. `scripts/to-v2-05-validate-local.ps1` — a safe exact-SHA Windows runner with mandatory `-ExpectedHead` and `-StageDir` (outside both repos). Validate expected private GitHub origin URLs, branch/upstream, exact local/remote feature HEAD and pinned TradeOps and DocFlow `origin/main`, clean trees, SDK/global.json 10.0.401. Verify StageDir/manifest and all six archived hashes/provenance, local read-only package metadata/dependency graph, no re-pack, no upload, no registry calls or credentials, and stage path not symlink/junction/inside checkout. Create disposable temp project and **NuGet.Config** with strict source mapping: only candidate DocFlow.* from local staged artifacts; other TradeOps.Application transitive dependencies from already approved public NuGet.org if necessary. Use temporary per-run NuGet packages cache, never put candidate archives in repo. Explicit warnings-as-errors restore/build and synthetic offline test executable. Refuse fallback to DocFlow ProjectReference or unpinned/unpublished PackageReference elsewhere.
3. `docs/orchestration/workstreams/TO_V2_05_STATUS.md` and `TO_V2_05_VALIDATION_NOTES.md` — actual source pins, inspected package manifest and separately reported candidate ZIP SHA-256, synthetic assertion count, false-positive/failure analysis and owner Windows reproduction.
4. Existing assignment remains unchanged; update existing Draft PR metadata/hand-off comment.

**No tracked extra `.csproj`**: TO-V2-04 all-project offline qualification pins exactly 15 existing TradeOps `.csproj`, so new compiled test project MUST be ephemeral, with `Program.cs` compiled by an external scratch project. Do not alter the TradeOps `src/` port/interface/HTTP adapter, its solution, tooling baseline, Workstream registry (orchestrator-owned), trading code, DocFlow-v2, any package metadata, workflow YAML or `Directory.*` files.

## Required probe assertions

- Verify **six actual staged bytes** against manifest SHA-256 before consumption; verify exact archive and NuSpec names, version, lib/net10.0 and exact closed package dependencies. Reject corrupt/tampered, missing, duplicate, wrong-source, wrong-version, symlink/reparse, unexpected feed or mixed-source artifacts, and mismatched manifest. Do not treat a generated manifest or old local payload equivalence as an independently signed release.
- Construct `DocFlowExtractionRequest` with representative owned `JsonElement` objects; map (in fixture/test only) to package API `RawExtractionRequest`, `SchemaExtractionRequest`, `BackendSelection`; call the **real package** `DocFlowExtractionFacade` with deterministic fake backend; obtain the two owned artifacts and convert them to `DocFlowExtractionResult`. Check canonical JSON/UTF-8, document ID, 64-digit fingerprint, schema validation state, source provenance, semantic deep equality and lack of unintended mutation, no extra normalization/validator copies; use the existing accepted TO-V2-01B0 design for boundary semantics.
- Check cancellation before dispatch, unsupported provider/model, bad JSON/schema and an invalid structured-result case if feasible, counting fake backend creation/invocations and ensuring zero real provider, DB, network or broker mutations.
- Explicitly distinguish package-only **test mapping** from an implemented `IDocFlowExtractionPort` runtime registration; do not add a production bridge, DI wiring or extra third-party runtime dependency. Maintain no real egress other than explicit public NuGet.org package restore for missing approved dependencies.
- Report whether local candidate ABI proof passes with exact TradeOps and candidate DocFlow source SHAs, and separately note no credentialed private GitHub Packages consumer/ACL was verified.

## Exit semantics and evidence

- **3 = LOCAL CANDIDATE ABI/CONTRACT PASS / PUBLISH BLOCKED / IN-PROCESS PRODUCTION INTEGRATION BLOCKED**; not equivalent to published private package consumption.
- **2 = FAIL / missing artifact / incomplete or ambiguous evidence / source mismatch / test errors**. Never use 0 to imply publication or production integration permission. For missing owner artifacts, say NOT RUN and give a complete fail-fast PowerShell command instead of fabricating success.
- Run both positive/negative synthetic cases and show exact **individual counts/assertions**, source-linked TradeOps project and package-only DocFlow assembly checks. Separate actual Windows owner-run results from worker static inspection.

## Permanent hard stops

**GitHub Actions OFF** in both repos, no workflow YAML, CI runner/workflow dispatch or hosted substitute. **NuGet PUBLISH BLOCKED / LICENSE APPROVAL BLOCKED**, no new licensed dependencies or package license edits, no GitHub Packages publishing, ACL/grants/token or rights inference. No TradeOps production in-process adapter, modular-monolith deployment, public DocFlow.Api HTTP exposure, live OIDC/LLM, operational DB, broker orders/testnet/trading or source pushes to public v1.

Worker must check source-of-truth refs, PRs, compare and existing design/packaging documents first; if main moves stop before silently relaxing pins. Do not create a new branch or PR; **worker does not merge**. Handoff: final exact GitHub HEAD, scope/changed filenames, reviews/tests, local execution command, remaining legal/registry/authenticated consumer gates. Development Orchestrator alone reviews and merges.
