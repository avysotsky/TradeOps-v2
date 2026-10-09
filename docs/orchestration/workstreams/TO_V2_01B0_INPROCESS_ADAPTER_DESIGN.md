# TO-V2-01B0 — In-Process DocFlow Adapter: Contract and Integration RFC

**Decision state:** DESIGN COMPLETE / IMPLEMENTATION BLOCKED BY DF-V2-04 RELEASE GATES / orchestrator review required.  
**Workstream:** TO-V2-01B0 (documentation only); **owner of future implementation:** TO-V2-01B, not this worker.  
**Date:** 2026-10-09 (Europe/Kyiv).  
**Repositories:** PRIVATE `avysotsky/TradeOps-v2` and PRIVATE `avysotsky/DocFlow-v2`. Archived public v1 repositories are out of scope.  
**Working branch:** `TradeOps/to-v2-01b0-inprocess-adapter-contract`; Draft [PR #3](https://github.com/avysotsky/TradeOps-v2/pull/3).  
**Observed upstream:** TradeOps-v2 `main=2a2558e6e9b49cbc539b8b85bc59c82271fbcea7`; DocFlow-v2 `main=0f009e79126a9beee5eef4b6eecbef1725937e12`. Both must be revalidated by the implementer. DF-V2-04 candidate branch PR #10 HEAD `7c68bdbbecf07a0c160874e36dfe6adce1a13ff1`, not merged or published.

## 0. Normative decisions, evidence and scope

**Selected seam:** the *unchanged* `TradeOps.Application.Models.IDocFlowExtractionPort` is resolved to *exactly one* TradeOps-owned adapter, `DocFlowHttpExtractionAdapter` **or** future `InProcessDocFlowExtractionAdapter`. The latter directly calls the published `DocFlow.Extraction.Application.IDocFlowExtractionFacade.ExtractRawAsync` in the same .NET process. It must never issue an internal HTTP request, start Python, invoke the DocFlow CLI, or duplicate normalization/validation.

This specification is executable guidance, **not evidence of an implemented adapter, authorized model account, package read access, public HTTP deployment, live-trading approval, or full JSON Schema Draft 2020-12 support.** The native DocFlow `SchemaSubset` rejects unsupported schema features rather than claiming Python Draft equivalence. Extraction is non-persistent and must not send broker orders.

**Verified live source (2026-10-09):**

- TradeOps `src/TradeOps.Application/Models/DocFlowExtractionContracts.cs`; `src/TradeOps.Infrastructure/DocFlow/{DocFlowHttpExtractionAdapter,DocFlowServiceCollectionExtensions}.cs`; `TradeOps.Infrastructure.csproj`; `TradeOps.Api/Program.cs`.
- DocFlow `src/DocFlow.Extraction.Application/{IDocFlowExtractionFacade,DocFlowExtractionFacade,ExtractionRequests,ExtractionArtifacts,IExtractionBackendFactory}.cs`; `src/DocFlow.SchemaExtraction/{SchemaExtractionRequest,NativeExtractionService,IExtractionBackend,IRequestAwareExtractionBackend,IConfidentExtractionBackend,ExtractionResultFactory,ValidationOutcome}.cs`; `src/DocFlow.Normalization/{TextDocumentNormalizer,TextDocumentNormalizer.Normalized}.cs`; `src/DocFlow.Api/Extraction/{ExtractionHttpOptions,ExtractionRequestParser,ExtractionHttpEndpoint}.cs`.
- `docs/orchestration/workstreams/TO_V2_01A_TOPOLOGY_DESIGN.md`, `DUAL_TOPOLOGY_ARCHITECTURE_PLAN.md`, `V2_WORKSTREAM_REGISTRY.md`, `WORKSTREAM_PROTOCOL.md`; DocFlow `DF_V2_02E_STATUS.md`, `DF_V2_03_STATUS.md` and DF-V2-04 branch `DF_V2_04_STATUS.md` / `docs/packaging/PRIVATE_NUGET_RELEASE_POLICY.md`. The older TO-V2-01A ADR inventory predates the *now merged* DocFlow native facade and secure HTTP endpoint; **live source above controls**.
- TradeOps `main` was one registry-only commit ahead of this worker's registration baseline when inspected (branch initially 1 ahead / 1 behind, merge base `8f1963e1dad4d78fc81b9985e8b0f00531c9814c`). No rebase needed for this disjoint, documentation-only branch; orchestrator decides integration.

## 1. Exact port contract and mapping (G3)

Actual immutable TradeOps boundary, unchanged by this workstream:

~~~csharp
public sealed record DocFlowExtractionRequest(
    JsonElement RawDocument, JsonElement SchemaRequest,
    string Provider, string Model, string? DocumentName = null);
public sealed record DocFlowExtractionResult(
    string NormalizedDocumentJson, string StructuredExtractionJson);
public interface IDocFlowExtractionPort
{
    Task<DocFlowExtractionResult> ExtractAsync(
        DocFlowExtractionRequest request, CancellationToken cancellationToken = default);
}
~~~

DocFlow source has `RawExtractionRequest(JsonElement RawDocument, SchemaExtractionRequest SchemaRequest, BackendSelection Backend, string? DocumentName)`, `BackendSelection(string Provider, string Model)`, and `ExtractionArtifacts` exposing owned `JsonElement NormalizedDocument` / `StructuredResult`. In-process calls `IDocFlowExtractionFacade.ExtractRawAsync`; **never** `ExtractNormalizedAsync` on the supplied raw document. The normalized overload is only for a *genuinely already normalized* and independently integrity-checked artifact; it is not an alternate API for the existing TradeOps port.

| TradeOps input | Exact target or policy | Required preservation / rejection |
|---|---|---|
| `RawDocument` (`JsonElement`) | `new RawExtractionRequest(raw.Clone(), schema, selector, name)` | Must be an object following raw DocFlow input shape. Keep source.provider / source_uri / source_document_id / time metadata, participants and ordered segments. No `document_id`, `segment_id` or `fingerprint` is added by adapter. Reject raw trying to carry normalized IDs (normalizer owns the raw shape). |
| `SchemaRequest` (`JsonElement`) | `SchemaExtractionRequest.Parse(schemaRequest.Clone())` | Envelope has **exactly** `schema_name` (string), `schema_version` (positive Int32), `json_schema` (schema object). Reject unknown/duplicate members; delegate Draft feature checks and root-object rules to the native parser/`SchemaSubset.Verify`, once. Retain normalized schema identity as parser produces it, never silently default a version or strip unsupported keywords. |
| `Provider` | `BackendSelection.Provider` | Case-sensitive `"openai"` or `"groq"` as current HTTP v1 ingress; also require exact configured host allowlist **and** `IExtractionBackendFactory.IsSupported`. No automatic fallback, normalization of names or provider credential transfer. Distinguish *extraction selector* from `rawDocument.source.provider` (document origin). |
| `Model` | `BackendSelection.Model` | Nonblank; ≤200 chars; already trimmed, no control characters or U+0085/U+2028/U+2029; case-sensitive match against *explicitly configured* (provider, model) allowlist and factory support. No implicit default model. |
| `DocumentName` | `RawExtractionRequest.DocumentName` | Null remains null; non-null 1–256 chars, not blank, no CR/LF. `NativeExtractionService` requires `IRequestAwareExtractionBackend` when a non-null documentName is supplied; reject instead of silently dropping metadata. |
| `CancellationToken` | Unchanged token, or linked caller/deadline token | Propagate into facade → native extraction → backend. Cancellation must never be converted into successful result. |
| `schemaVersion: 1` | **HTTP-only** root request/response field | NOT a property of the in-process port or native facade; in-process adapter must **not** wrap raw document with a transport envelope. Distinct from caller `schemaRequest.schema_version` (positive schema metadata) and package SemVer. |
| `NormalizedDocumentJson` | `artifacts.NormalizedDocument.GetRawText()` | Complete JSON object, owned for return lifetime. Preserve `document_id`, `fingerprint`, title, document_type, source provenance and timestamps, participants and participant_id, segments and segment_id, sequence and text; do not rehash, reorder, recanonicalize, reconstruct or trim. |
| `StructuredExtractionJson` | `artifacts.StructuredResult.GetRawText()` | Complete JSON object. Preserve `engine`, `document_type`, `data`, optional nullable `confidence` and `validation_status` plus `validation` status/checks/error diagnostic fields. Do not revalidate to improve or rewrite the result. |

**Source-owned identity:** `TextDocumentNormalizer.Normalize` generates `textdoc:<sha256>` from canonical source identity, `textseg:<sha256>` from canonical document/segment identity and lowercase SHA-256 `fingerprint` from canonical content (including source, participants, segments). It normalizes timestamp offsets and sorts raw segments by sequence. `TextDocumentNormalizer.ValidateNormalized` verifies IDs and fingerprint, returning the original normalized structure without rewriting it. Both are DocFlow responsibilities, not TradeOps ones. For already normalized inputs, corrupted document_id, segment_id, fingerprint, participant references, sequence or canonical text must fail before backend dispatch. In raw mode, injected `document_id`/`fingerprint` keys must be rejected as unknown input fields rather than trusted.

**Exact structured shape observed in `ExtractionResultFactory` / `ValidationOutcome`:**

~~~json
{
  "engine": "schema_driven_text_v1:<backend.Name>",
  "document_type": "<normalized.document_type>",
  "data": {"...": "backend-owned JSON object"},
  "confidence": null,
  "validation_status": "valid",
  "validation": {
    "status": "valid",
    "checks": {
      "json_schema": {
        "status": "passed",
        "message": "Backend data satisfies the caller JSON Schema.",
        "details": {}
      }
    },
    "confidence": 1.0
  }
}
~~~

For schema-invalid **object-shaped** backend data: `validation_status="invalid"`, `validation.status="invalid"`, `checks.json_schema.status="failed"`, validation.confidence = 0.0 and `details.errors[]` entries each have `path`, `schema_path`, `validator`, `message`. The **top-level** `confidence` comes from optional `IConfidentExtractionBackend.Confidence`, can be null and must be finite within [0,1]; **do not confuse** it with numerical `validation.confidence` (schema check indicator). Engine uses `backend.Name` (which need not equal provider/model selector); do not overwrite it with `Provider`. A schema-invalid result is still an extraction *result*, not a fabricated exception or valid signal.

**JSON ownership and precision:** clone both caller `JsonElement` objects before any caller-owned `JsonDocument` can be disposed; `SchemaExtractionRequest.Parse` clones its internal schema; `ExtractionArtifacts` clones both results. No `JsonDocument`-backed element may escape a disposed lifetime. Use `GetRawText()` for each final JSON string, **not** `Deserialize<object>`, `JsonNode` reconstruction, `double` conversion, stringified nested JSON or serializer configuration that changes numbers; JSON numbers, booleans, nulls and arrays remain typed. The HTTP v1 response is `{schemaVersion:1, normalizedDocument:object, structuredResult:object}`; existing HTTP adapter extracts exactly those two objects as strings. Compare `JsonDocument` trees structurally, preserving array order, **numeric representations/precision** (never double-coerce) and all required/unknown-field distinctions; object key order and insignificant whitespace are non-semantic. If an accepted numeric comparison policy considers lexical variants (e.g., 1 vs 1.0) equivalent, it must be separately frozen with strict decimal/big-integer test evidence; default is lossless lexeme comparison.

## 2. Topology, dependency direction and DI contract (G3 → G4)

~~~text
Distributed:
  TradeOps.Api → TradeOps.Application → IDocFlowExtractionPort
     → TradeOps.Infrastructure.DocFlowHttpExtractionAdapter
     → authenticated HTTPS /api/v1/extractions
     → DocFlow.Api extraction endpoint → IDocFlowExtractionFacade
     → IExtractionBackendFactory.Create(fresh) → native extraction

Single-PID modular-monolith (target composition owned by TO-V2-01C):
  TradeOps.Monolith.Host → TradeOps.Application → IDocFlowExtractionPort
     → TradeOps.Infrastructure.DocFlow.InProcessDocFlowExtractionAdapter
     → [same DI container] IDocFlowExtractionFacade.ExtractRawAsync
     → IExtractionBackendFactory.Create(fresh) → native extraction

Native DocFlow-only graph:
  DocFlowExtractionFacade → TextDocumentNormalizer.Normalize (RAW: once)
     → NativeExtractionService.ExtractNormalizedAsync
     → TextDocumentNormalizer.ValidateNormalized (identity integrity)
     → backend.ExtractAsync (exactly once) → SchemaSubsetDiagnostics
     → ExtractionResultFactory → ExtractionArtifacts
~~~

In both modes `TradeOps.Application` and `TradeOps.Domain` depend only on TradeOps-owned models/interfaces; neither references a DocFlow package. **Only TradeOps Infrastructure's in-process adapter + the authorized composition root** take the three package references. The existing HTTP client remains available for the *separate*, explicitly selected distributed host, not registered in the monolith as a fallback.

**Proposed composition API (names not yet implemented):**

~~~csharp
// Composition-root-only pseudocode; not a change to application contracts.
switch (explicitTopology) // required config: DocFlow:Topology = Http|InProcess
{
  case "Http":
    services.AddTradeOpsDocFlowHttp(config);  // existing typed HttpClient
    break;
  case "InProcess":
    ValidateNoDocFlowInternalUrlOrFallback(config);
    services.AddScoped<IDocFlowExtractionFacade, DocFlowExtractionFacade>();
    services.AddSingleton<IExtractionBackendFactory, ApprovedAllowlistedBackendFactory>();
    services.AddScoped<IDocFlowExtractionPort, InProcessDocFlowExtractionAdapter>();
    break;
  default: throw new InvalidOperationException("Explicit DocFlow topology required");
}
ValidateExactlyOneRegistration<IDocFlowExtractionPort>(services);
~~~

`ApprovedAllowlistedBackendFactory` is **illustrative**, not a shipped provider or a claim it exists. DocFlow's facade has no production default backend. No host can declare in-process readiness without a genuine allowed factory and policy. Both factory and host (service identity/policy) must independently approve exact provider/model pairs; unknown pairs fail **before** backend construction. For purely offline tests use only deterministic fake model identifiers explicitly allowed by a synthetic test factory; production must never register this fake as a success fallback.

**Registration contract:** explicitly one `IDocFlowExtractionPort` binding per process and one selected topology at startup; `IServiceCollection` check + runtime post-build `IEnumerable<IDocFlowExtractionPort>` cardinality/DI validation in a scope (without accidentally causing provider egress) to defeat last-registration-wins. Reject any unrecognized/missing topology, conflicting registration, configured internal-HTTP URL in InProcess, missing factory, absent package and invalid/unapproved selection policy. Configure `ValidateOnBuild` and `ValidateScopes` where supported. No recovery path that replaces an in-process DI failure with loopback HTTP, CLI, Python or another model. Keep dedicated `TradeOps.Api` distributed-mode behavior independently controlled.

**Lifetime table:**

| Component | Preferred lifetime | Justification/guard |
|---|---|---|
| `IDocFlowExtractionPort` in-process adapter | scoped (or transient if truly stateless) | No per-request mutable static/global state, no capture by singleton; scoped host operations. |
| `IDocFlowExtractionFacade` / concrete facade | scoped | Holds factory only; scoped aligns with current DocFlow.Api registration and future request-specific dependencies. Could become transient after review, not assumed singleton. |
| `IExtractionBackendFactory` | immutable thread-safe singleton **only if** pure policy and thread-safe | `IsSupported` pure/no provider I/O. If factory has tenant-scoped state, register scoped and ensure facade captures same scope, *not* singleton. |
| `IExtractionBackend` / request-aware / confidence backend | newly created **per operation** | Never reuse; optional `Confidence` is instance-local state. A request gets one backend instance, one extraction invocation. |
| Activity/logging/options | immutable singleton or per-scope context | No raw document, secrets, tenant/request state in static cache, logs or traces. |
| Work item | new `IServiceScope` per background operation | Dispose scope after awaited extraction. Do not pass scoped services outside scope. |

The current `DocFlowExtractionFacade` does **not** dispose a backend produced by `IExtractionBackendFactory.Create` (backend interfaces have no disposable requirement). Hence **resource-owning production backends must have an explicit DocFlow-owned lifetime/disposal decision** (e.g., factory returns a scoped managed object whose scope is disposed after call; or a separately versioned facade/backend disposal contract approved upstream). Merely adding `using` around the TradeOps adapter does not fix backend lifetime and must not cause double disposal. The implementer must inspect released package's actual lifecycle and present an owner-approved resource disposition test; this is a **gate**, not a claimed current capability.

**Concurrency and bulkhead:** enforce process-local bounded concurrency/deadline at *composition/adapter ingress*, at most 4 by default for deterministic parity with current DocFlow.Api (whose options allow 1..32); ownership/configuration may differ after capacity testing. Acquire with cancellation and release in `finally`, never use one globally shared mutable backend or `Confidence` state. For fair parity testing set same concurrency/deadline in both paths; excess simultaneous requests should produce a documented, fail-closed `transient_failure` / resource-exhausted category rather than silently queue unbounded work. If HTTP intentionally returns 429 immediately, match policy for the comparable in-process test rather than accepting timing nondeterminism as a parity failure.

## 3. Intended adapter algorithm (pseudocode, NOT implemented)

~~~csharp
public async Task<DocFlowExtractionResult> ExtractAsync(
    DocFlowExtractionRequest input, CancellationToken callerToken = default)
{
    ArgumentNullException.ThrowIfNull(input);
    callerToken.ThrowIfCancellationRequested();
    // Transport parity guard only: current HTTP 1 MiB serialized v1 body,
    // 256 KiB schema request, bounded depth/nodes/participants/segments,
    // strict duplicates, source-limited provider/model/documentName.
    // Parse and check inbound shape WITHOUT running a second schema validator.
    var raw = input.RawDocument.Clone();
    var schemaEnvelope = input.SchemaRequest.Clone();
    EnforceHttpV1EquivalentIngressLimitsAndShape(raw, schemaEnvelope,
        input.Provider, input.Model, input.DocumentName);
    var schema = SchemaExtractionRequest.Parse(schemaEnvelope); // sole schema authority
    var selector = new BackendSelection(input.Provider, input.Model);
    RequireExplicitHostAndFactorySelection(selector); // no network in IsSupported
    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
    deadline.CancelAfter(approvedDeadline); // distinguish origin of cancellation
    await concurrencyGate.AcquireAsync(deadline.Token);
    try
    {
        var request = new RawExtractionRequest(raw, schema, selector, input.DocumentName);
        var artifacts = await facade.ExtractRawAsync(request, deadline.Token);
        deadline.Token.ThrowIfCancellationRequested();
        ValidateBoundedResponseEnvelope(artifacts); // SHAPE and SIZE ONLY
        return new DocFlowExtractionResult(
            artifacts.NormalizedDocument.GetRawText(),
            artifacts.StructuredResult.GetRawText());
    }
    catch (OperationCanceledException) when (callerToken.IsCancellationRequested) { throw; }
    catch (OperationCanceledException) when (deadline.IsCancellationRequested)
    { throw SanitizedPortFailure("timeout"); }
    catch (Exception ex) when (IsExpectedFailure(ex))
    { throw ClassifySanitizeAndPreserveCorrelation(ex); }
    finally { concurrencyGate.Release(); }
}
~~~

The skeleton is **illustrative**: implementer must ensure acquisition exceptions do **not** call Release without successful ownership and disposal covers a fail-before-entry branch (a boolean lease / `try/finally` inside acquired scope is required). `ValidateBoundedResponseEnvelope` checks JSON object shape, required `document_id`, `fingerprint` and `validation_status`, v1 envelope byte count ≤2,097,152; it is **not** a second schema validation or ID recomputation. Never modify a returned artifact to fit a limit. For exact HTTP parity, account for serialized wrapper `schemaVersion:1` and property names and request `documentName:null` when computing byte budgets; do not measure only raw field lengths. Distinguish `CallerCancellation` from local `DeadlineExceeded` and explicit provider `TimeoutException`. Do not rely on exception message text to classify remote provider failures.

**Ingress equivalence issue discovered in source:** `DocFlow.Api.Extraction.ExtractionRequestParser` is internal to `DocFlow.Api`, not packaged. It rejects duplicate JSON members *recursively*, trailing commas/comments, depth >32, >12,000 nodes, JSON envelope >1,048,576 UTF-8 bytes, schema envelope >262,144 bytes, >1,024 raw segments or >512 participants; it also validates exact v1 fields. The public facade does not implement **all** those transport quotas and `TextDocumentNormalizer.RequireKeys` does not by itself reject duplicate raw JSON members. Future TO-V2-01B **must implement the same bounded transport-shape policy at its own ingress or negotiate a reusable DocFlow-owned non-API ingress-policy package** with a separately gated contract. Do not import `DocFlow.Api` into the monolith or duplicate `SchemaSubset`. For ambiguous duplicate input already materialized as `JsonElement`, inspect all nested `EnumerateObject` members, fail closed; never use `TryGetProperty` alone to assume uniqueness.

## 4. Failure taxonomy and compatibility boundaries

Public `IDocFlowExtractionPort` method signature and two-string success result remain unchanged. Future Infrastructure may introduce a **TradeOps-owned sanitized `DocFlowExtractionFailureException`** (proposed) with stable `Code` and optional retryability, without changing the port interface. A missing concrete typed exception today is **not** permission to leak inner provider exception messages, auth tokens, raw body, traces, stack or connection strings.

| Condition / source | Proposed TradeOps category | HTTP reference / interpretation |
|---|---|---|
| Null/non-object raw, malformed/trailing/duplicate JSON, invalid shape/metadata, forbidden extra keys, invalid schema envelope, selector syntax, exceeded traversal | `invalid_request` | HTTP 422 `invalid_request`; request >1 MiB yields 413 `request_too_large`; distinguish size using same guard. No backend invocation. |
| Unsupported native schema keyword/dialect/root/type (fail-closed) | `unsupported_schema` | HTTP 422 `unsupported_schema` when parser recognizes specific native unsupported error; see classification gap below. Never ignore the keyword. |
| Unknown/disallowed provider or model, missing factory / unsupported selection | `provider_unavailable` | HTTP 503 `provider_unavailable`. Host config failures should fail startup; no default provider. |
| Schema-conformant backend data | SUCCESS / `validation_status=valid` | HTTP 200 with untouched structured+normalized objects. |
| Backend output object but invalid against supported schema | SUCCESS / `validation_status=invalid` | HTTP 200 with full diagnostics; **do not promote invalid to valid**. |
| Backend returns non-object, invalid `Confidence` (NaN/±Inf/<0/>1), inconsistent or malformed artifacts, oversized output | `extraction_failed` | HTTP 502 `extraction_failed`; keep source's diagnostics internal only. |
| Caller cancellation (token) | propagate `OperationCanceledException` | HTTP abort typically 499 `cancelled`; no artifacts, no retry; do not mask as timeout. |
| Explicit adapter deadline elapsed | `timeout` | HTTP 504 `timeout`; catch different origin than caller cancellation. |
| Provider timeout / `TimeoutException` | `timeout` | HTTP 504 `timeout`. |
| Transient provider transport/bulkhead rejected | `transient_failure` | HTTP 503 or 429 `transient_failure`, stable category separate from unsupported provider. |
| Unexpected backend/factory exception | `extraction_failed` | HTTP 502 sanitized; do not claim transient without typed evidence. |

**Known classification gap, needs G3 tests:** today DocFlow native schema `SchemaExtractionRequest.Parse` can throw `ArgumentException`, while internal HTTP parser classifies `unsupported_schema` by a small set of message prefixes; the facade also uses `ArgumentException` for selector/model and backend validation. The TradeOps HTTP adapter currently discards all non-success HTTP bodies into a generic `DocFlowHttpExtractionException`. Therefore **HTTP/InProcess negative-error equivalence cannot yet be asserted through both port adapters**. The B implementer must define a safe fixed-code normalization strategy and test `code` / caller cancellation / HTTP status without editing the frozen port. Avoid fragile message-prefix rules in production; propose a DocFlow-owned typed unsupported-schema exception/structured failure contract for a future approved version, or implement a strictly bounded contract-tested taxonomy consistent with the current API until that is available. **No changes to DocFlow package/API are authorized by B0.**

No automatic retries or provider substitution in either topology. A cancellation or transport failure must never call a second backend, never trigger a broker route, never be treated as an approved research decision. Risk `preview` or execution `dry-run` is informational even when a synthetic preview is green; neither authorizes submission of real orders.

## 5. No hidden HTTP, Python, storage or broker side effects

**In-process adapter and its DocFlow facade chain:** zero `HttpClient` directed to DocFlow API/localhost, zero `Socket` for internal service routing, zero process launches (`Process.Start`, `python`, `pythonnet`, CLI), zero DocFlow controller invocations. Permit **only** explicitly configured provider egress from a real DocFlow provider adapter as a **separate** network/security policy, after provider availability/credentials have been authorized. No actual provider adapter is shipped by the currently verified native facade and DF-V2-04 package candidate. For G3, synthetic factory performs **zero external I/O**, including LLM calls, DNS and HTTP.

**Instrumentation plan:** inject a fail-fast `HttpMessageHandler` / fake `IHttpClientFactory` into any host-internal named/typed HTTP registrations to flag unexpected DocFlow traffic, and an explicit counter for all approved fake provider invocations. Use a network-disabled test/container and process-start probe/audit where feasible; do not infer zero traffic from lack of visible HTTP requests alone. Assert `internalDocFlowHttpRequests=0`, `pythonProcesses=0`, `externalProviderHttpRequests=0` for isolated G3. Do not pretend a failing message handler alone intercepts raw sockets or subprocesses: static dependency scan + sandbox process/network monitoring are complementary.

**TradeOps side effects:** `brokerOrderRequests=0`, `brokerCancels=0`, `persistenceWrites=0` and `TradeOpsDbContext.SaveChanges` calls = 0 for G3/G4/G5 fixtures. Tests must use fake broker and fake/throwing persistence repositories, not live exchange/network accounts. **Important live source limitation:** `TradeOps.Api/Program.cs` currently executes `dbContext.Database.MigrateAsync()` at startup and depends on a configured PostgreSQL connection. A future monolith acceptance test cannot safely boot that default host to claim zero DB mutations: use a separately composed test-only host that omits migrations, or require a TO-V2-01C-approved startup refactor/guard before single-PID tests. Do not bypass the gate by pointing it at a real database. No DB schema, migrations, production host or broker code is changed in B0.

## 6. Private NuGet dependency gate (G5; DF-V2-04 is concurrent)

As of checked DF-V2-04 Draft PR #10 HEAD `7c68bdbbecf07a0c160874e36dfe6adce1a13ff1`, the following **locally packed candidate set** passed its exact-HEAD Ubuntu/Windows isolated consumer proof; **no artifact is published and no package-read ACL has been demonstrated**:

| Candidate ID | Target | Exact dependencies (candidate only) |
|---|---|---|
| `DocFlow.Normalization` | net8.0 | none |
| `DocFlow.SchemaExtraction` | net8.0 | `DocFlow.Normalization [2.0.0-preview.1]` |
| `DocFlow.Extraction.Application` | net8.0 | `DocFlow.Normalization [2.0.0-preview.1]` and `DocFlow.SchemaExtraction [2.0.0-preview.1]` |

`2.0.0-preview.1` is an **unpublished candidate**, *not* accepted canonical release version or an authorized `PackageReference`. Full package archive hashes of local builds are not necessarily stable because generated ZIP metadata changes; CI proved stable code/nuspec/README/PDB payloads, **not** registry immutability. Never use test-pack hash as a released artifact lock.

**Release/consumer checklist (mandatory before TO-V2-01B starts):**

1. Development Orchestrator reviews DF-V2-04 PR #10, exact final HEAD CI, appropriate license/proprietary redistribution/supply-chain/SourceLink/BOM policy, package ID/version collision checks and merges only when separately approved. Verify merged-main CI. Protected manual publisher separately authorized; **no publish on PR or normal push**.
2. Verify actual private GitHub Packages owner/registry (candidate `https://nuget.pkg.github.com/avysotsky/index.json`), visibility, permissions, immutable version existence and publisher ability; no overwrite or `--skip-duplicate` workaround. Grant `avysotsky/TradeOps-v2` **package-specific Actions read-only** access in each package's **Package settings → Manage Actions access → Add repository**, matching GitHub's supported access model. Repo privacy is not sufficient.
3. First perform real authenticated package-only restore in an isolated TradeOps-v2 GitHub Actions runner with `permissions: contents: read; packages: read` and approved token mechanism. No PAT in git, artifacts or logs. Validate package source mapping `DocFlow.*` **only** to verified private registry, non-DocFlow package sources independently constrained; reject dependency confusion and unapproved transitive packages.
4. Pin all three to the **actually published and verified same version**, exact resolved dependency graph in project/central package management, check in consumer `packages.lock.json` **with the actual released package content hashes** and require `dotnet restore --locked-mode` for CI/release. Record immutable publisher commit, SBOM/license decisions and artifact SHA-256. Qualify clean Windows/Linux/Docker net8.0 restore/runtime, RID-specific provider-native dependencies and environment-limited credentials; no implicit .NET 10 upgrade.
5. Only after separate security acceptance allow real provider adapter registration. If no legitimate provider factory exists, the in-process host must fail readiness rather than return fake successful trading facts. On version failure roll back to an independently verified earlier immutable release; **no previous release currently exists**, so first-release rollback = disable in-process deployment, not silently switch HTTP.

**Developer-only option:** a separately authorized sibling-repository aggregate workspace with relative `ProjectReference` can prototype compile/debug of the native seam; it is **not the B production dependency, release fallback or a substitute for package/ACL G5**. Do not vendor DLLs/sources, create git submodules, add a package to `TradeOps.Application`, or reference `DocFlow.Api` / persistence modules. The native package graph contains no DB/storage, HTTP host, Python, TradeOps or broker dependency.

## 7. Deterministic G3 verification matrix (future TO-V2-01B)

Use fixed, committed UTF-8 fixtures with known inputs and expected **structural** normalized/structured JSON, fixed synthetic `IRequestAwareExtractionBackend + IConfidentExtractionBackend`. Factory records selectors, identities and backend instance GUIDs; every test asserts exact create/extract counts, statuses, exception categories, and zero network/process/broker/persistence counters. Selectors must be explicitly allowed in both test hosts. `HTTP` uses a properly secured HTTPS TestServer and test-only identity/scope, **not public listeners**. Reuse same native facade and same fake backend behavior; **never compare two live LLM generations**.

| ID | Fixture / stimulus | Required deterministic assertion |
|---|---|---|
| G3-01 | Raw document with mixed segment order, source_uri and source_document_id, timestamp offset, participant IDs, Unicode whitespace; strict schema with integer/string fields | Exactly one Normalize and one backend call; source identity, lowercase SHA-256 IDs and fingerprint match upstream golden outputs; final segment order and full nested fields match HTTP structurally. No adapter-side recompute. |
| G3-02 | Valid object-shaped data and nullable confidence | `validation_status=valid`, `validation.status=valid`, `checks.json_schema.status=passed`, `validation.confidence=1.0`, top-level confidence **null**. Keep engine and exact data numeric values. |
| G3-03 | Object-shaped backend data violates a supported `required`/`type`/`minimum` constraint | `validation_status=invalid`, `validation.status=invalid`, `checks.json_schema.status=failed`, `validation.confidence=0.0`, preserved diagnostic entries path/schema_path/validator/message; still a successful extraction response and **no downstream order**. |
| G3-04 | Unsupported keyword (`unevaluatedProperties` or unsupported Draft dialect) | `unsupported_schema` before factory Create; no ignored keyword and no Python fallback. |
| G3-05 | Bad raw root/extra normalized `fingerprint`, malformed schema envelope, unknown and recursive duplicate keys, trailing content, oversized limits | Exact `invalid_request` / `request_too_large` policy; zero factory Create. Disposed caller `JsonDocument` after cloning does not corrupt output. |
| G3-06 | Forge normalized document_id / segment_id / fingerprint / source / participant or sequence and attempt `ExtractNormalizedAsync` upstream | DocFlow integrity rejection before backend; adapter **does not** call normalized overload for TradeOps raw request. Any raw forged fields rejected, not re-labeled. |
| G3-07 | Unknown provider, case-different/forbidden model, unavailable factory, disallowed documentName and legacy backend incapable of forwarding it | Stable `provider_unavailable` or `invalid_request` per exact stage, zero backend call when rejected before Create; no selector default/fallback. |
| G3-08 | Pre-cancel, cancel during fake provider, cancel after provider return, adapter deadline, provider `TimeoutException` | No result after cancellation, caller cancellation propagated, timeout separately categorized; exactly ≤1 Create/Extract per attempted operation, zero retries. |
| G3-09 | Confidence `0`, `0.42`, `1`, `null`; then `-0.1`, `1.1`, NaN, Infinity, non-object data | First four preserved; other values → `extraction_failed` with sanitized error; never accept nonfinite/invalid confidence. |
| G3-10 | 30–100 concurrent requests with shuffled independent selectors, source IDs and confidence | Factory produces a **distinct backend per admitted operation**; no provider/name/confidence/doc identity cross-contamination or shared mutable state; bounded overload and cancellation documented, no double invocation. |
| G3-11 | Deterministic identical request through HTTP and in-process with same fixture/policy | Compare entire structural `normalizedDocument` and `structuredResult` (and transport `schemaVersion=1` separately); preserve all IDs, numbers, statuses, validation diagnostics. No unexpected/missing object fields; record field-level diff if failure. |
| G3-12 | Error/limit matrix across both topologies | Compare fixed HTTP status/code and in-process sanitized category (or explicitly record `BLOCKED` cases until typed-code gap resolved); malformed/body size, unsupported keyword, disallowed selector, schema-invalid 200, timeout/cancel. |
| G3-13 | Internal HTTP spy + process/network monitor, offline factory, fake broker/repositories | Internal DocFlow HTTP = 0, Python subprocess = 0, provider external HTTP = 0, live broker requests = 0, writes/migrations = 0; no real trading credentials configured. |
| G3-14 | Package-only clean consumer Linux + Windows + container, corrupted lock, unavailable private feed | Authenticated mapped restore from immutable set, valid signed-off content hashes; corrupted lock / missing ACL / unapproved package rejected; source checkout not needed. |
| G3-15 | Backend throws after creation; factory returns null; resource-owning backend | Sanitized `extraction_failed` or `provider_unavailable` according to phase; one Create, no retry; explicit DocFlow-owned disposal proof (currently unresolved), no leaked per-call resources. |

**Minimum auditable evidence:** commit SHA and CI run at **exact adapter-code HEAD**; dedicated test names/counts and golden JSON fixture hashes; factory Create/Extract count log; per-operation backend instance IDs; HTTP-vs-inprocess JSON structural diff (zero differences for positive subset); HTTP status/category evidence for negatives or explicit classification blocker; 0 internal HTTP/process/provider/broker/write counters from instrumented isolated test host; `NuGet.Config` source mapping + locked restore hash / effective Actions package read ACL evidence; dependency diff confined to infrastructure/composition/test ownership; no secrets, external provider, IBKR, real DB or hidden fallback.

## 8. Sequencing / ownership: G3, G4, G5 and security

| Gate/workstream | Owner and prerequisite | Exit / prohibited shortcuts |
|---|---|---|
| DF-V2-02E / DF-V2-03 | DocFlow-v2 upstream; native facade and secure extraction HTTP already merged on current DocFlow main | Native API is available; HTTP host is **not** public-release approved and has no production backend factory. |
| DF-V2-04 / G5 | DocFlow worker + Development Orchestrator | Exact-head package proof green, but **PUBLISH BLOCKED** until legal, registry visibility/immutability, package-specific TradeOps read ACL and real authenticated cross-repo restore; future B cannot bypass. |
| **TO-V2-01B / G3** | Future independent TradeOps worker after G5 and B0 design approval | Implement isolated `IDocFlowExtractionPort` in-process adapter, package references, bounded ingress, DI/failure/cancellation, deterministic G3 matrix; do not change port/Application/Domain. |
| **TO-V2-01C / G4** | Composition worker after G3 | Owns single-PID `TradeOps.Monolith.Host` and exclusive wiring, independent deployable services, startup migration safety and no hidden loopback; no real broker or DB mutations in tests. |
| **TO-V2-01D / parity** | Topology equivalence worker after B/C | Controlled `DocFlow → ResearchDecision → RebalancePlan → RebalanceOrderIntent → risk preview → execution dry-run` fixture, identical frozen policy/snapshot, structurally equal semantic result and provenance; explicit `mutationPerformed=false`, `brokerRequestSent=false`, `persistencePerformed=false`. Fake broker/DB only; never submit orders. |

**Whole-host deployment block remains:** `DocFlow.Api`'s legacy `DocumentsController` upload/read/storage routes are not mandatorily authenticated; secure extraction endpoint additionally requires production OIDC issuer/audience, verified `docflow.extract` scope **and** service-client identity, TLS/listener isolation and approved allowlists. An immutable **private** NuGet package does not secure a publicly reachable HTTP host, solve service-principal ACL policy or authorize real provider egress. Never advertise public deployment until separately reviewed/fixed; never equate a green risk preview with broker execution permission.

## 9. Orchestrator decision checklist

- [ ] Accept exact mapping, raw vs normalized path and schema/identity ownership; approve any requested transport ingress validation sharing without adding DocFlow.Api dependencies.
- [ ] Decide typed sanitized error taxonomy and the HTTP error-code gap; do not assert full negative parity until a suitable contract exists.
- [ ] Approve per-operation backend resource/disposal policy, immutable provider/model allowlist and bounded concurrency/timeout limits.
- [ ] Confirm DF-V2-04 **actually publishes** reviewed immutable artifacts, licenses and privacy gates, grants TradeOps **read-only** package ACL, and proves authenticated locked restore in TradeOps.
- [ ] Assign TO-V2-01B (then C, D) with exclusive ownership, deterministic no-mutation gates and new exact-head CI. No production deployment/trading or DB changes in B0.
- [ ] Review this Draft PR's docs-only diff and merge **only** through Development Orchestrator.

**Scope guarantee of this RFC:** creates documentation only; does not modify production code, tests, CI, schemas, shared registry, private package publication, repository visibility, or actual broker/persistence behavior.
