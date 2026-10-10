# TO-V2-05 — VALIDATION_NOTES

**Scope:** independent TradeOps-owned .NET 10 **test-only** consumer gate for six already-retained DF-V2-07 archives. NOT a NuGet publisher or a production DocFlow in-process adapter. Windows acceptance **NOT RUN** by this worker.

## Immutable coordinates and consumer topology

- TradeOps-v2 main: **0b2a5216c5528cb49d41eb9f5a43870e935da3ef**.
- DocFlow-v2 main: **62fa3c3c77134cf923233e51fcba7c4f08eb2ef7**.
- Archived DF-V2-07 **candidate source**: **927eddee08d084e7759eef1b0a67404711d2b3b1**; not the current DocFlow main.
- Existing feature branch: TradeOps/to-v2-05-offline-docflow-package-consumer-gate; registered HEAD 79ccaa885fb6ec9774a88c8e101e2a437a5f9cde; existing [Draft PR #7](https://github.com/avysotsky/TradeOps-v2/pull/7). Consult PR #7 for **latest exact reviewed HEAD** before running; never generate it by an unreviewed local recomputation.
- .NET SDK **10.0.401**, net10.0; TradeOps source remains at **15** tracked projects. Script rejects altered project inventory and stale pins.
- Read-only concurrent DF-V2-10: [DocFlow Draft PR #16](https://github.com/avysotsky/DocFlow-v2/pull/16). Security TestServer characterization and package ABI compatibility are distinct gates.

Temporary external test project has **one ProjectReference to actual TradeOps.Application.csproj** (which owns IDocFlowExtractionPort), and **three exact PackageReference entries** for locally staged DocFlow candidates. It never references DocFlow source projects. No permanent sixteenth project; no TradeOps src changes; no production adapter, provider credentials, DI registration, HTTP, database or broker.

## Retained stage contract — six original ZIP files

Version **2.0.0-preview.1**, source repository **https://github.com/avysotsky/DocFlow-v2** and NuSpec commit **927eddee08d084e7759eef1b0a67404711d2b3b1** are mandatory. The StageDir contains manifest.json and artifacts/ with **only** these six real files:

    DocFlow.Normalization.2.0.0-preview.1.nupkg
    DocFlow.Normalization.2.0.0-preview.1.snupkg
    DocFlow.SchemaExtraction.2.0.0-preview.1.nupkg
    DocFlow.SchemaExtraction.2.0.0-preview.1.snupkg
    DocFlow.Extraction.Application.2.0.0-preview.1.nupkg
    DocFlow.Extraction.Application.2.0.0-preview.1.snupkg

Retained manifest shape is strictly:

    {
      "owner": "avysotsky",
      "source_sha": "927eddee08d084e7759eef1b0a67404711d2b3b1",
      "version": "2.0.0-preview.1",
      "sha256": {
        "<exact filename 1>": "<64 lowercase hex SHA256>",
        "... all six exact archive filenames ...": "<64 lowercase hex SHA256>"
      }
    }

Those example hash values are placeholders, **not verified archive hashes**. The validator does not generate or alter this manifest. It computes a fresh SHA-256 of each retained archive and compares it with the original manifest; then checks exact NuSpec metadata, ZIP entry allowlist, archive size/path/symlink limitations, net10.0 DLL/portable PDB, source repository SHA and exact dependency graph. ZIPs are read, never unpacked into the repository, modified or repacked.

**Closed DocFlow package graph:**

| ID | Exact internal NuGet dependencies |
| --- | --- |
| DocFlow.Normalization | none |
| DocFlow.SchemaExtraction | DocFlow.Normalization [2.0.0-preview.1] |
| DocFlow.Extraction.Application | DocFlow.Normalization [2.0.0-preview.1], DocFlow.SchemaExtraction [2.0.0-preview.1] |

NuGet.Config is created only in scratch: DocFlow.* maps **exclusively** to the candidate-local stage. Previously approved Microsoft.* and System.* dependencies may be restored from public nuget.org. A **per-run, initially empty NUGET_PACKAGES cache** prevents accidental reliance on global or previously staged DocFlow packages. The runner verifies the locked DocFlow package coordinates and content hashes, deliberately tampers with a lock entry and demands rejection, restores the original lock, re-runs locked restore and compares SHA-256 of **each extracted DLL** with the bytes inside its original verified nupkg. The source project references are checked to prevent a false source-linked DocFlow facade proof.

## Synthetic behavior and expected assertions

The **actual package** DocFlowExtractionFacade receives a representative TradeOps DocFlowExtractionRequest with caller-owned JsonElements. The test-only Map method clones input JSON, invokes SchemaExtractionRequest.Parse, creates RawExtractionRequest plus BackendSelection, calls the real ExtractRawAsync with a deterministic request-aware fake backend, and returns the two actual artifact JSON strings as a TradeOps DocFlowExtractionResult. It performs no second normalization or schema validation in mapping.

| Case | Expected positive/negative behavior |
| --- | --- |
| ABI-01 | real TradeOps port type/signature; three package assembly identities; forbidden source dependency absent |
| ABI-02 | canonical JSON structural equality with package normalizer, UTF-8 round-trip, source provenance, source-generated document_id, segment_id and 64-char lowercase fingerprint; native validation valid; one fake dispatch, request not mutated |
| ABI-03 | normalized and structured JSON remain valid after both caller JsonDocuments are disposed |
| ABI-04 | already canceled token rejects before fake backend creation or call |
| ABI-05 | external provider, unknown model, empty provider and control-character model fail before backend creation |
| ABI-06 | empty segments, forged document_id, zero schema version, duplicate schema name, array root schema fail before backend creation |
| ABI-07 | schema-invalid but object-shaped fake data remains an **invalid** native structured result, with validation diagnostics rather than false acceptance |

**Expected output:** 7 unique per-case PASS lines plus exact summary with cases=7, passed=7, failed=0, and at least 40 individual assertions. Expected exit **3**. **Actual owner execution: NOT RUN; actual assertions: NOT RUN; actual SHA-256 for each of six retained ZIPs: NOT READ.** This worker's sandbox does not contain the owner Windows stage, PowerShell 5.1 or .NET SDK 10.0.401. Static source review is not execution evidence.

## Owner Windows acceptance reproduction

Use an **existing** DF-V2-07 stage with original manifest. The following example path was recorded by the orchestrator but **must be confirmed to exist; do not silently regenerate any archives**. After receiving the exact final feature SHA from PR #7, substitute that immutable value in the ExpectedHead argument.

    $ErrorActionPreference = 'Stop'
    $to = 'D:\Projects\TradeOps-v2'
    $df = 'D:\Projects\DocFlow-v2'
    $stage = 'D:\DocFlowCandidates\DF-V2-07-20261010-193712'
    if (-not (Test-Path -LiteralPath (Join-Path $stage 'manifest.json') -PathType Leaf)) {
        throw 'Select the actual retained DF-V2-07 stage; do not repack'
    }
    git -C $to fetch origin
    git -C $df fetch origin
    git -C $to switch TradeOps/to-v2-05-offline-docflow-package-consumer-gate
    git -C $to pull --ff-only origin TradeOps/to-v2-05-offline-docflow-package-consumer-gate
    git -C $df switch main
    git -C $df pull --ff-only origin main
    Set-Location $to
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\to-v2-05-validate-local.ps1 -ExpectedHead '<EXACT FINAL PR #7 SHA>' -StageDir $stage
    $code = $LASTEXITCODE
    Write-Host "TO-V2-05 exit: $code"
    if ($code -ne 3) { throw "Offline candidate ABI gate not passed; exit $code" }

Do not infer that the hardcoded example stage exists. If missing, this is **NOT RUN / exit 2**; collect owner evidence rather than repack. The validator fails closed for an unexpected origin, branch, upstream, source-main SHA, dirty checkout, mismatched SDK, altered project count, missing/modified archives, wrong dependency graph, bad NuGet source resolution or any synthetic failure. It restores the caller's NuGet environment and removes only its own temporary scratch directory. Original candidate files and the manifest remain unmodified.

Owner should save the stdout transcript, the **six individually printed ARCHIVE VERIFIED ... SHA256=... lines**, the three PACKAGE DLL SHA256 lines, the single lock-tamper rejection, the seven ABI PASS lines, the assertion total and **exit 3**. Send the evidence to Development Orchestrator for review before any merge.

## Failure/false-positive analysis

- An editable manifest + matching ZIPs is **local byte integrity evidence**, not a signed, owner-approved source release. Altering both together is outside the trust established by this gate; actual provenance is bounded by the preserved source commit in NuSpec plus owner custody.
- Binary ZIP SHA-256 and portable PDB metadata are checked on **the retained archives**, not on rebuilt packages. DF-V2-07 observed that full ZIP hashes can vary across equivalent repacks; do not rebaseline to a second pack run or to the current DocFlow main.
- Package-only successful local restore **does not** test GitHub Packages private registry, authenticated reader ACL, package signature, unused namespace/version, licensing or redistribution authority. Private publication remains blocked.
- Test-only fake backend guarantees no actual provider or broker mutations but **does not** qualify configured real provider/model allowlists, backend resource disposal, HTTP v1 ingress limits, tenant boundaries, host concurrency, G3/G4 DI composition, error classification or production in-process topology.
- NuGet.org is the only permitted external restore endpoint for already-approved non-DocFlow dependencies. Offline means candidate binaries are local and no runtime provider/service access, **not** guaranteed zero internet requests during missing Microsoft package restore.
- A missing stage, stale main, nonclean trees, SDK conflict, invalid package data, missing assembly, NuGet lock violation or synthetic failure is **FAIL / exit 2**, never a green pass. No failure is silently skipped; never substitute a synthetic archive/hash for missing owner artifacts.
- Distinct DF-V2-10 whole-host security risks remain: package ABI success says nothing about public DocFlow.Api authorization or safe deployment.

## Immutable policy

**GitHub Actions OFF. NuGet PUBLISH BLOCKED. LICENSE APPROVAL BLOCKED.**

No workflow edit/dispatch, hosted substitute, registry publication, credentials/ACL changes, production adapter, public listener, real OIDC/LLM/provider, operational database, broker, testnet/order flow, new licensed dependency or TradeOps/DocFlow public-v1 update.

Final decision belongs to Development Orchestrator. Only **exit 3 + owner-reviewed evidence** constitutes local candidate ABI research acceptance; it does not authorize any release or production integration.

## Main-head drift discovered after implementation

As of the worker's final GitHub recheck, DocFlow-v2 origin/main is **ced3bd5fd985634345b764e02ebb45d14f8341a6**, not the assigned immutable **62fa3c3c77134cf923233e51fcba7c4f08eb2ef7**. DF-V2-10 PR #16 was merged; compare from the pinned main changed six **test/document/script-only** files and no DocFlow library implementation. TradeOps-v2 main stayed unchanged. The TO-V2-05 script intentionally still checks the **old approved SHA** and therefore returns **exit 2** after a current fetch. The executable acceptance example above is **blocked until Development Orchestrator explicitly authorizes and registers a new read-only DocFlow-main baseline**. Do not workaround by changing local refs, bypassing the SHA guard or rebuilding/replacing the original six DF-V2-07 candidate ZIPs. GitHub Actions / NuGet publishing / licensing remain blocked.
