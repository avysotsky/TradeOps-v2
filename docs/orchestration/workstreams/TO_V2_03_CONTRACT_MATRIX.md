# TO-V2-03 — .NET 10 native HTTP contract matrix

**Implementation:** tools/DualHttpContract/Program.cs, executed by
scripts/to-v2-03-validate-local.ps1. **Execution state: NOT RUN.**
This is the inventory of implemented assertions, not a claim of passing tests.

## Implementation boundaries

| Boundary | Real production implementation | Test-only dependency |
|---|---|---|
| TradeOps port and HTTP adapter | IDocFlowExtractionPort / DocFlowHttpExtractionAdapter; 1 MiB outbound body, 2 MiB inbound body | HttpClient connected to in-memory TestServer |
| DocFlow HTTP | DocFlow.Api POST /api/v1/extractions and native parser, JWT authorization, semaphore, deadlines | WebApplicationFactory with HTTPS request scheme, no bound TCP socket |
| Native extraction | DocFlowExtractionFacade, TextDocumentNormalizer, NativeExtractionService, SchemaSubset, result factory | IExtractionBackendFactory and per-call fake backend with fixed confidence 0.42 |
| OIDC | Genuine JwtBearer middleware and scope/client policy | Locally issued signed JWT / static signing keys, no OIDC discovery |
| Persistence | DocFlow.Api boot registration | DbContext and options registrations removed in fixture, no DB |
| Storage | Real LocalFileStorage constructor | Unique disposable temporary root |
| Adversarial remote HTTP response | Genuine TradeOps HTTP adapter | Separately isolated static HttpMessageHandler; **not** a native DocFlow-host response |

Neither TradeOps.Api nor TradeOps.Worker starts. No broker, LLM, Postgres,
real OIDC, deployed service endpoint, trading operation or NuGet publication.

## Owner evidence and TestServer bootstrap gate (2026-10-10)

- At **old TradeOps HEAD 158b069ce0eb46bb5935d3654da577b62a87d8c2**
  with DocFlow-v2 main 3f09e91ba356fb367248ef1b48a56b19d9fdae97,
  SDK 10.0.401: NuGet restore **PASS**, real net10 dependencies and
  temporary DualHttpContract **BUILD PASS**.
- **8/8 cases FAILED immediately with InvalidOperationException;
  assertions=0; exit code=1.** The old harness suppressed exception
  details; this is a common TestServer bootstrap failure before real
  HTTP assertions, not 8 independent negative contract results.
- New runner checks actual DocFlow.Api.deps.json, pins test content
  root outside the scratch .sln tree, and performs an explicit real
  TestServer /health/live bootstrap probe. Failure of that bootstrap
  yields bootstrap_failed=1, not_run=8 with redacted diagnostics and
  nonzero exit. **New-head restore/build/test execution NOT RUN**.
- In the original case matrix below, each NOT RUN status applies to
  the **current unverified head**; the previous old-head attempts did
  execute but failed before reaching those scenario assertions.
- The intentionally strict JWT rejection of another nonempty client_id
  remains unchanged; it has not yet been exercised by the owner's run.

---

## Eight test cases implemented (new HEAD: all NOT RUN)

| Case | Genuine DocFlow endpoint/facade? | Expected contract checked | Evidence |
|---|---|---|---|
| canonical-native-parity-utf8 | Yes | Full JSON deep equivalence against direct facade for normalized and structured output; document_id, fingerprint, ordered segment_id, participants, provenance, confidence, schemaVersion 1, validation status and Unicode/emoji; one backend invocation; no stored file | NOT RUN |
| invalid-provider-data | Yes | Schema-invalid well-formed output remains HTTP 200 / validation_status=invalid and full direct facade parity | NOT RUN |
| jwt-security-https | Yes | Missing/malformed/expired/wrong issuer/audience/key JWT -> 401; wrong scope and absent client ID -> 403; unapproved nonempty client ID **must also be rejected** (known upstream blocker); HTTP request scheme -> 403; pre-dispatch rejection; valid synthetic JWT accepted | NOT RUN |
| request-contract-and-bounds | Yes | HTTP wrong version, duplicate key, unsupported schema, bad normalization -> 422; unsupported selection -> 503; oversized request rejected by adapter before send; DocFlow HTTP -> 413 for Content-Length and chunked input; malformed JSON and invalid UTF-8 -> 422; inclusive **exactly 1,048,576-byte** native request -> 200 | NOT RUN |
| http-response-and-bounds | Host for oversized native response; fake external handler for adapter-only cases | Native oversized output -> 502; adapter rejects non-2xx, malformed JSON, version mismatch, missing fields, >2 MiB response; error strings never echo provider sentinel | NOT RUN |
| backend-errors-and-routing | Yes | Backend exception -> 502, transport exception -> 503, disabled HTTP route -> 404, missing backend -> 503, no retries | NOT RUN |
| deadline-and-cancellation | Yes | Native deadline -> 504 mapped to sanitized adapter error, caller cancellation and HttpClient timeout propagate, no retry | NOT RUN |
| bounded-concurrency | Yes | Maximum concurrency=1: second request -> 429 before backend dispatch, adapter sanitizes failure, initial request completes after release | NOT RUN |

The harness is intended to emit per-case PASS/FAIL and:
TO-V2-03 TOTAL cases=8 passed=N failed=M assertions=K,
returning exit code 1 for one or more failures. **N/M/K are unknown until executed.**

## Source-discovered security blocker (requires upstream decision)

The current READ-ONLY DocFlow.Api policy (ExtractionHttpEndpoint.cs) checks for
a **nonempty** client_id/azp/appid claim and scope=docflow.extract, but it does
**not** bind the identity claim to an approved TradeOps client ID. A differently
named client carrying a valid signature/scope is therefore accepted by the
current predicate. The jwt-security-https scenario now contains an explicit
403 expectation for an unapproved nonempty client ID. From source inspection
this expectation is **likely to FAIL (HTTP 200)** when the owner executes it.
This is not a tested failure count (test execution remains NOT RUN).
We deliberately do not override auth policy in TestServer or silently weaken
this assertion. A separately authorized DocFlow security change, or a formally
reviewed contract decision, is required before this gate can be accepted.

## Contract distinctions and residual gaps

- The TradeOps adapter does not retrieve service JWTs itself. The test's injected
  HttpClient carries a synthetic signed JWT. This does not prove production
  credential lifecycle or OIDC integration.
- Non-2xx DocFlow codes are intentionally converted by the existing TradeOps
  adapter into a generic DocFlowHttpExtractionException; numeric 422/429/502/503
  and 504 are not observable by the application port. Do not misreport a typed
  status-preserving client contract.
- DocFlow still configures a DbContext at startup, but the fixture removes
  DbContext/options registrations after service configuration. Production DB
  behavior and migrations are not exercised. Real LocalFileStorage constructs
  a sandbox root; route must leave it empty.
- The test-only external handler for >2 MiB and malformed *received* responses
  proves only client-side defensive behavior. Real DocFlow responds 502 before
  sending an oversized result, so claiming it transmitted >2 MiB would be false.
- No socket TLS/reverse proxy, external service JWT acquisition, real model
  throughput, provider retries, production logging sink, deployment security,
  historical Python equivalence, private NuGet publication/ACL, or brokerage
  behavior is certified by these assertions.
- Both participating libraries are **source-referenced**, not copied or vendored.
  TO-V2-01B in-process adapter remains outside this scope.

## Owner validation

From D:\Projects\TradeOps-v2 on Windows 10 / Visual Studio 2026 with
D:\DotNet\dotnet.exe SDK 10.0.401, after refreshing Git remotes and checking
out the assigned branch and read-only DocFlow main, execute:

    & .\scripts\to-v2-03-validate-local.ps1 -ExpectedTradeOpsHead '<exact 40-hex PR #5 HEAD>'

The PowerShell script enforces the expected branch, clean Git trees, exact
TradeOps-v2 PR head and reviewed main, DocFlow-v2 pinned main,
.NET10 target frameworks, 15 existing TradeOps csproj files and SDK 10.0.401.
It creates and deletes a temporary csproj outside both repositories and
does not invoke any project run targets besides the isolated harness.
Re-pin any moved source SHA only after Development Orchestrator review.
