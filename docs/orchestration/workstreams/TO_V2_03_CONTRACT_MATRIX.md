# TO-V2-03 — .NET 10 native HTTP contract matrix

**Implementation:** tools/DualHttpContract/Program.cs, executed by
scripts/to-v2-03-validate-local.ps1. **NEW DOCFLOW MAIN 7e9dc589: ALL 8 TO-V2-03 SCENARIOS NOT RUN; prior 7/8 against OLD main is historical.**
This is the contract inventory and evidence; it does not claim all tests pass.

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

## DF-V2-08 post-merge contract matrix — NEW SOURCE GRAPH (2026-10-10)

Read-only DocFlow-v2 main PIN:
`7e9dc58947797251ab3be188411f514a6b078ac3`.
DF-V2-08 independently passed 9/9 native HTTP and 12/12 facade
tests (owner evidence). This does not constitute TO-V2-03 runtime proof.
TradeOps-v2 existing PR #5 includes a test-only early-configuration
bridge for the new native client-identity authorization contract.

| Test boundary | Test-only config / actual native requirement |
|---|---|
| JWT positive | signed synthetic client_id=tradeops-integration-test, matching the explicit server-owned JSON allowlist |
| JWT negative | signed synthetic client_id=unapproved-service-client must yield HTTP 403 BEFORE fake backend dispatch |
| Early minimal-host config | fresh per-WebApplicationFactory appsettings.json containing true AllowedClientIds JSON array, not deferred parent scalar |
| Host bootstrap | only flat environment=Testing and isolated contentRoot in host configuration; true DocFlow.Api assembly and deps manifest |
| Backend / normalization | genuine TradeOps HTTP adapter + real DocFlow.Api TestServer/DocFlowExtractionFacade; fake external provider only |
| Storage and DB | per-fixture disposable storage; DocFlowDbContext removed; unreachable synthetic DB registration |

Reason for fixture design: DF-V2-08 verified that forwarding indexed
`AllowedClientIds:0` via DeferredHostBuilder.UseSetting or deferred
AddInMemoryCollection may add an empty parent scalar, which native
DocFlow correctly rejects. Loading appsettings.json via
WebApplication.CreateBuilder(args) from an isolated temporary
contentRoot avoids that projection. The new JSON config owns
`AllowedClientIds=["tradeops-integration-test"]`.

Expected genuine DF-V2-08 Testing-only diagnostic for every enabled
host: `DF-V2-08 APP EARLY CONFIG: childCount=1 keys=[0] parentScalar=null`.
This is a required inspection on owner Windows, NOT yet observed
by TO-V2-03. No token/client ID values should appear in logs.

**NEW source-linked Windows verification:** .NET SDK 10.0.401
restore **NOT RUN**; Release build **NOT RUN**; native TestServer
startup and all 8 HTTP scenarios **NOT RUN**; assertion total unknown.
Previous 7 PASS / 1 FAIL / 168 assertions / exit 1 was against
TradeOps-v2 bd06bbacc46c1fbba4c06832d92984ba4aec6d9e
and DocFlow-v2 OLD main 3f09e91ba356fb367248ef1b48a56b19d9fdae97.
Do not conflate that historical result with new validation.

Owner must run scripts/to-v2-03-validate-local.ps1 with exact
current PR #5 TradeOps SHA and pinned DocFlow-v2 main above.
8/8 PASS, 0 FAIL, exact SDK, successful build and exit 0
are required before any acceptance. Failure stays blocking.
GitHub Actions OFF; no workflow changes, DocFlow-v2 read-only,
TradeOps production code untouched, no merge or NuGet publish.

---
## Latest owner-local evidence: tested TradeOps bd06bba (HISTORICAL old DocFlow main)

Exact test source: TradeOps-v2 bd06bbacc46c1fbba4c06832d92984ba4aec6d9e
and DocFlow-v2 3f09e91ba356fb367248ef1b48a56b19d9fdae97.
Windows SDK 10.0.401; clean/pinned inputs PASS; source-linked restore
PASS (1.2s), native DocFlow/TradeOps/DualHttpContract net10 Release
build PASS (4.5s), real in-memory TestServer bootstrap PASS.

| Scenario | ACTUAL result at tested HEAD |
|---|---|
| canonical-native-parity-utf8 | PASS |
| invalid-provider-data | PASS |
| jwt-security-https | **FAIL — actual HTTP 200, required 403** |
| request-contract-and-bounds | PASS |
| http-response-and-bounds | PASS |
| backend-errors-and-routing | PASS |
| deadline-and-cancellation | **PASS** |
| bounded-concurrency | PASS |
| TOTAL | **7 PASS / 1 FAIL / 168 assertions / exit 1** |

Confirmed blocking contract mismatch: a correctly signed, correctly
scoped token with client_id=unapproved-service-client returns 200
from the native DocFlow.Api even though the strict negative test
expects 403. The current endpoint checks a nonempty client ID but
does not restrict the service identity to an explicit allowlist.
That token remains valid under the currently implemented policy,
so the rejection requirement needs a separate orchestrator-approved
DocFlow-v2 authorization workstream or explicit security decision.
**Never alter/skip the TO-V2-03 negative test to obtain PASS.**

The cancellation scenario is now proven PASS with real native backend
cancellation observations and server deadline mapping. Earlier
snapshot results below remain historical, not the latest run.

This worker must not edit DocFlow-v2. If upstream authorization is
updated, re-pin DocFlow source SHA and repeat all eight scenarios
only after independent orchestration review. GitHub Actions OFF;
no production API/Worker, broker/LLM, DB, NuGet push or merge.

---
## Owner Windows SDK 10.0.401 TestServer results (previous 6/8 snapshot)

TradeOps a596a250c6646755969dd332be6145a84680f044 against
DocFlow 3f09e91ba356fb367248ef1b48a56b19d9fdae97:
restore PASS, complete net10 Release build PASS, bootstrap PASS,
6 of 8 HTTP cases PASS, 2 FAIL, 153 assertions executed, exit 1.

| Case | Actual old-HEAD result |
|---|---|
| canonical-native-parity-utf8 | PASS |
| invalid-provider-data | PASS |
| jwt-security-https | FAIL: expected 403 assertion |
| request-contract-and-bounds | PASS |
| http-response-and-bounds | PASS |
| backend-errors-and-routing | PASS |
| deadline-and-cancellation | FAIL: unexpected adapter HTTP exception |
| bounded-concurrency | PASS |

Security failure is now observed, while the actual numeric return status
was not displayed in that run. Native DocFlow-v2 source indicates
allowed client identity is not pinned, likely returning HTTP 200.
Strict expected 403 is preserved for upstream orchestrator review.

Cancellation test had assumed OperationCanceledException only.
TradeOps adapter maps non-2xx HTTP to DocFlowHttpExtractionException.
Updated test-only case now verifies native server 504 on deadline,
and requires independently observed native backend token cancellation
for caller abort and HttpClient timeout before accepting either
transport cancellation or sanitized HTTP rejection.
The previous substep was not identified by the diagnostic logger.

NEW HEAD outcomes, including restore/build, are NOT RUN.
Historical 6/8 is not acceptance of the updated code. No merge.

---
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

## Eight test cases implemented (NEW source graph: all NOT RUN)

| Case | Genuine DocFlow endpoint/facade? | Expected contract checked | Evidence |
|---|---|---|---|
| canonical-native-parity-utf8 | Yes | Full JSON deep equivalence against direct facade for normalized and structured output; document_id, fingerprint, ordered segment_id, participants, provenance, confidence, schemaVersion 1, validation status and Unicode/emoji; one backend invocation; no stored file | NOT RUN (new DocFlow pin) |
| invalid-provider-data | Yes | Schema-invalid well-formed output remains HTTP 200 / validation_status=invalid and full direct facade parity | NOT RUN (new DocFlow pin) |
| jwt-security-https | Yes | Missing/malformed/expired/wrong issuer/audience/key JWT -> 401; wrong scope and absent client ID -> 403; unapproved nonempty client ID **must also be rejected** (known upstream blocker); HTTP request scheme -> 403; pre-dispatch rejection; valid synthetic JWT accepted | NOT RUN (new DocFlow pin) |
| request-contract-and-bounds | Yes | HTTP wrong version, duplicate key, unsupported schema, bad normalization -> 422; unsupported selection -> 503; oversized request rejected by adapter before send; DocFlow HTTP -> 413 for Content-Length and chunked input; malformed JSON and invalid UTF-8 -> 422; inclusive **exactly 1,048,576-byte** native request -> 200 | NOT RUN (new DocFlow pin) |
| http-response-and-bounds | Host for oversized native response; fake external handler for adapter-only cases | Native oversized output -> 502; adapter rejects non-2xx, malformed JSON, version mismatch, missing fields, >2 MiB response; error strings never echo provider sentinel | NOT RUN (new DocFlow pin) |
| backend-errors-and-routing | Yes | Backend exception -> 502, transport exception -> 503, disabled HTTP route -> 404, missing backend -> 503, no retries | NOT RUN (new DocFlow pin) |
| deadline-and-cancellation | Yes | Real native HTTP 504 for server deadline and sanitized adapter rejection; caller cancellation and HttpClient.Timeout must cause actual native backend cancellation, allowing transport OCE or sanitized non-2xx only with that proof; no retry | NOT RUN (new DocFlow pin) |
| bounded-concurrency | Yes | Maximum concurrency=1: second request -> 429 before backend dispatch, adapter sanitizes failure, initial request completes after release | NOT RUN (new DocFlow pin) |

The harness is intended to emit per-case PASS/FAIL and:
TO-V2-03 TOTAL cases=8 passed=N failed=M assertions=K. On OLD DocFlow main with tested TradeOps bd06bba: **N=7, M=1, K=168, exit code=1**. For NEW pinned DocFlow 7e9dc589: **N/M/K NOT RUN**.

## Source-discovered security blocker (requires upstream decision)

The current READ-ONLY DocFlow.Api policy (ExtractionHttpEndpoint.cs) checks for
a **nonempty** client_id/azp/appid claim and scope=docflow.extract, but it does
**not** bind the identity claim to an approved TradeOps client ID. A differently
named client carrying a valid signature/scope is therefore accepted by the
current predicate. The jwt-security-https scenario now contains an explicit
403 expectation for an unapproved nonempty client ID. From source inspection
this expectation is **likely to FAIL (HTTP 200)** when the owner executes it.
At tested bd06bba against OLD DocFlow: **HTTP 200**, expected 403. Upstream DF-V2-08 is now merged with an actual allowlist and independent 9/9 native HTTP proof; TO-V2-03 must still demonstrate real 403 and no dispatch on the NEW pinned source.
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
