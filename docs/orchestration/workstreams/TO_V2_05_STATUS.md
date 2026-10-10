# TO-V2-05 — Independent Offline DocFlow NuGet Consumer Contract Gate: STATUS

**Implemented in existing PRIVATE TradeOps-v2 Draft PR #7. Owner Windows candidate acceptance: NOT RUN. No merge.**

## Source pins and comparison

| Reference | Immutable SHA |
| --- | --- |
| TradeOps-v2 origin/main | 0b2a5216c5528cb49d41eb9f5a43870e935da3ef |
| DocFlow-v2 origin/main | 62fa3c3c77134cf923233e51fcba7c4f08eb2ef7 |
| DF-V2-07 **local candidate source**, not current DocFlow main | 927eddee08d084e7759eef1b0a67404711d2b3b1 |
| Registered worker starting HEAD | 79ccaa885fb6ec9774a88c8e101e2a437a5f9cde |
| Existing branch | TradeOps/to-v2-05-offline-docflow-package-consumer-gate |
| SDK / TFM | 10.0.401 / net10.0 |

At registration, GitHub compare of the feature branch against TradeOps main returned **ahead 1 / behind 0**, only assignment file added. The live exact implementation HEAD must be taken from [existing Draft PR #7](https://github.com/avysotsky/TradeOps-v2/pull/7), and used verbatim for the owner's ExpectedHead parameter. If main moves, refuse to silently repin.

Parallel [DocFlow-v2 Draft PR #16](https://github.com/avysotsky/DocFlow-v2/pull/16), DF-V2-10, tests whole-host security and does not overlap this TradeOps-only worker.

## Exact file scope

1. tools/DocFlowCandidateConsumerProbe/Program.cs — standalone synthetic package ABI test compiled only in a temporary external project; references genuine TradeOps.Application.Models.IDocFlowExtractionPort and the real candidate DocFlowExtractionFacade; implements **no production adapter or DI registration**.
2. scripts/to-v2-05-validate-local.ps1 — mandatory ExpectedHead and StageDir; exact source/SDK/15-project checks, read-only archive SHA-256/ZIP/NuSpec/dependency validation, source-mapped isolated NuGet restore, lock tamper rejection, candidate DLL hash equality, warnings-as-errors build, seven synthetic cases, deterministic exit semantics.
3. docs/orchestration/workstreams/TO_V2_05_STATUS.md — this status.
4. docs/orchestration/workstreams/TO_V2_05_VALIDATION_NOTES.md — reproduction and verification ledger.

Pre-existing assignment unchanged. No changes to production src, DocFlow repo, workflow YAML, package versions, Directory.* files, orchestrator registry, solution files, or tracked csproj files. TradeOps remains at exactly **15** tracked projects; consumer csproj is created and removed outside both repositories.

## Offline candidate matrix

All three library package IDs are net10.0, version **2.0.0-preview.1**:

| Candidate ID | Required exact DocFlow dependencies |
| --- | --- |
| DocFlow.Normalization | none |
| DocFlow.SchemaExtraction | DocFlow.Normalization [2.0.0-preview.1] |
| DocFlow.Extraction.Application | DocFlow.Normalization [2.0.0-preview.1], DocFlow.SchemaExtraction [2.0.0-preview.1] |

The original local DF-V2-07 stage contains a four-key manifest (owner, source_sha, version, sha256) and six archives, one .nupkg plus one .snupkg per package ID. The runner requires a full **SHA-256 of each of the six physical archived ZIP files** matching the retained manifest, matching NuSpec Git commit, library net10.0 DLL and portable symbols, no unexpected dependencies or source. These physical SHA-256 values **are not accessible in the worker environment**; no values have been fabricated or repacked. The manifest itself is not independently signed.

## Expected validation vs actual execution

| Validation | Required result | Actual |
| --- | --- | --- |
| Six retained archive SHA-256 checks, ZIP metadata, exact dependency closure | 6/6 | **NOT RUN** |
| Candidate-only local NuGet restore and lock tamper rejection | PASS | **NOT RUN** |
| Locked restore and 3 extracted DLL hash matches | PASS | **NOT RUN** |
| Release warnings-as-errors compilation, temp consumer | PASS | **NOT RUN** |
| ABI-01 assembly/port boundaries | PASS | **NOT RUN** |
| ABI-02 canonical raw mapping and JSON/provenance | PASS | **NOT RUN** |
| ABI-03 owned JsonElement lifetime | PASS | **NOT RUN** |
| ABI-04 pre-cancellation before backend creation | PASS | **NOT RUN** |
| ABI-05 invalid selectors fail before backend creation | PASS | **NOT RUN** |
| ABI-06 invalid raw/schema fail before backend creation | PASS | **NOT RUN** |
| ABI-07 invalid structured schema result retains native invalid status | PASS | **NOT RUN** |

The fixture must report **seven distinct PASS cases and at least 40 individual assertions**. Actual executed count is **NOT RUN**, not 7/7. A real Windows owner run is necessary because the worker cannot access D:\DocFlowCandidates or D:\DotNet\dotnet.exe. GitHub source/compare and contract design were inspected, not substituted for executable tests.

## Exit semantics and blockers

**3** = LOCAL CANDIDATE ABI/CONTRACT PASS, but PUBLISH BLOCKED and IN-PROCESS PRODUCTION INTEGRATION BLOCKED.

**2** = incomplete, invalid or ambiguous evidence; missing stage, altered ZIP/hash/provenance, SDK/source mismatch, bad restore/build/fixture.

**0** = intentionally never returned; no claim of published-package integration.

**GitHub Actions OFF; NuGet PUBLISH BLOCKED; LICENSE APPROVAL BLOCKED.** No hosted runner, workflow edits, private package publication, package grants, live providers, trading, broker operations, operational databases, public HTTP deployment or production in-process adapter. Authenticated private GitHub Packages locked restore, TradeOps reader ACL, actual approved license/redistribution rights, SourceLink/SBOM/signing and separate G3/G4 production acceptance remain unverified.

See TO_V2_05_VALIDATION_NOTES.md for exact owner commands, archive filenames and negative-case interpretation.

## Subsequent upstream movement detected before owner acceptance (2026-10-10)

**HARD STOP — DO NOT RELAX PIN AUTOMATICALLY.** Subsequent read-only GitHub recheck found DocFlow-v2 main advanced from the assignment-pinned **62fa3c3c77134cf923233e51fcba7c4f08eb2ef7** to **ced3bd5fd985634345b764e02ebb45d14f8341a6**. GitHub compare reports **1 commit ahead, 0 behind**, with six changed files confined to DF-V2-10 assignment, status, validation notes, PowerShell script, and test-only TestServer project. DF-V2-10 PR #16 is merged, with upstream owner acceptance described as seven cases, 121 assertions and exit 3. No production source-package code changes in this compare, but the immutable main SHA **still differs** from the authorized TO-V2-05 pin.

TradeOps-v2 main remains **0b2a5216c5528cb49d41eb9f5a43870e935da3ef**. The worker **did not repin** the DocFlow main expectation and **did not run** the local consumer test. With current origin/main, the validator intentionally **fails with exit 2** before touching package archives. Development Orchestrator must review/authorize this new read-only main baseline and any corresponding separate worker update before owner Windows acceptance. This is not permission to change the candidate package source SHA **927eddee08d084e7759eef1b0a67404711d2b3b1**.
