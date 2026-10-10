# TO-V2-03 — Worker implementation status / orchestrator handoff

**State: OWNER 6 PASS / 2 FAIL, 153 ASSERTIONS; CANCELLATION TEST UPDATED; NEW-HEAD NOT RUN — Draft PR #5 / DO NOT MERGE.**
Date: 2026-10-10 (Europe/Kyiv). GitHub is source of truth.

## 2026-10-10 — Owner TestServer run, 6 PASS / 2 FAIL (latest)

Owner tested TradeOps-v2 a596a250c6646755969dd332be6145a84680f044
with DocFlow-v2 3f09e91ba356fb367248ef1b48a56b19d9fdae97.
SDK 10.0.401, exact checkout guards PASS, restore PASS (1.4s),
all real .NET 10 project builds plus DualHttpContract build PASS (6.8s).
TestServer health bootstrap PASS using genuine DocFlow.Api content root.

| Scenario | Actual owner outcome at previous HEAD |
|---|---|
| canonical-native-parity-utf8 | PASS |
| invalid-provider-data | PASS |
| jwt-security-https | FAIL: HTTP status mismatch, expected 403 |
| request-contract-and-bounds | PASS |
| http-response-and-bounds | PASS |
| backend-errors-and-routing | PASS |
| deadline-and-cancellation | FAIL: DocFlowHttpExtractionException in adapter |
| bounded-concurrency | PASS |
| TOTAL | 6 PASS / 2 FAIL / 153 assertions / exit 1 |

JWT failure is observed. Current native DocFlow-v2 allows nonempty
client_id with proper scope but has no allowed-service identity pin.
The harness still requires 403 for unapproved nonempty client_id.
Development Orchestrator must decide separate upstream authorization change.

Deadline case previously accepted only OperationCanceledException for
caller cancellation and HttpClient timeout. In-memory TestServer may
surface a cancelled native HTTP request as non-2xx, which the genuine
TradeOps adapter maps to DocFlowHttpExtractionException.
The previous log did not identify the exact substep.
New test-only case first checks native HTTP 504 for server deadline;
then validates sanitized adapter rejection without retries.
Client cancellation and HttpClient timeout now require that the native
fake backend independently observes its cancellation token signaled.
Either transport cancellation or a sanitized HTTP rejection is valid
only with that native observation; success or unrelated errors fail.
Test also checks elapsed timeout, absence of retries and request safety.
Failure messages now include only numerical expected/actual HTTP status.

NEW HEAD: restore, build and eight HTTP scenarios NOT RUN.
Passing/failing assertions at new HEAD unknown. Final SHA is in the
latest PR #5 worker comment after this status commit.
PR remains Draft. GitHub Actions OFF. No merge.

---
## 2026-10-10 — owner Windows TestServer bootstrap failure / worker isolation fix (prior)

**State: BOOTSTRAP MITIGATION COMMITTED; NEW-HEAD WINDOWS RE-RUN REQUIRED; MERGE BLOCKED.**

### Actual owner Windows execution: old TradeOps HEAD

Reviewed source: TradeOps branch 158b069ce0eb46bb5935d3654da577b62a87d8c2,
DocFlow-v2 read-only main 3f09e91ba356fb367248ef1b48a56b19d9fdae97,
SDK 10.0.401.

| Phase | Actual owner result |
|---|---|
| Exact SHA / SDK / 15-project / net10 guards | PASS |
| Temporary NuGet restore | PASS (2.9 s) |
| Native DocFlow and TradeOps dependency builds | PASS |
| DualHttpContract temporary Release build | PASS (7.9 s overall build) |
| Harness process | LAUNCHED |
| Case report | **8 FAIL, 0 PASS, 0 assertions** |
| Exception category | InvalidOperationException, redacted by older runner as unexpected test exception |
| Validator exit code | **1 (FAIL)** |
| Real HTTP endpoint / adapter / JWT / facade assertions | NOT EXERCISED: exception before any assertion |

This is a common early TestServer failure, **not** proof of eight contract
defects. Do not report success or passed assertions for this source.

### Diagnosis (source-based, not a captured runtime stack)

The executable harness runs from a new temporary directory outside
either source solution. WebApplicationFactory normally discovers
the content root via a test setting/assembly manifest or parent .sln;
solution fallback cannot find a parent .sln from this scratch tree.
It also expects DocFlow.Api.deps.json beside the temporary test runner.
The previous exception handler erased the failure detail, so these are
probable shared bootstrap blockers, **not a verified single exception
message**.

### Fix limited to test infrastructure

- PowerShell pins ASPNETCORE_TEST_CONTENTROOT_DOCFLOW_API to the
  absolute, verified DocFlow.Api directory for the TestServer lifetime.
  Restores the previous environment setting in finally. This is **not
  an HTTP listener binding**.
- Checks that the temporary executable output contains the **real**
  DocFlow.Api.deps.json dependency manifest. If missing, copies only
  the built release manifest from the pinned, read-only DocFlow.Api
  checkout into the disposable sandbox. Missing authentic metadata
  remains fail-closed.
- Harness now first calls the real DocFlow.Api /health/live route
  inside an isolated TestServer. Failed bootstrap produces a dedicated
  testhost-bootstrap message and summary bootstrap_failed=1,
  not_run=8, exit 1; it does not fabricate eight HTTP case results.
- SafeFailure classifies the known missing-solution, missing-deps
  and configuration error families; unknown failure is reported only
  by safe exception type and declaring-type origin, **not** by arbitrary
  exception message, JWT, provider error, raw document or stack trace.
- Eight business contract scenarios are unchanged, including the
  unapproved nonempty client_id => HTTP 403 negative assertion. That
  stricter requirement remains pending an upstream DocFlow-v2
  policy decision by Development Orchestrator.
- No production TradeOps or DocFlow source modification; no real
  HTTP socket, broker/LLM, database, OIDC, GitHub Actions, workflow
  YAML, NuGet publish, in-process adapter or merge.

### Evidence at NEW HEAD

Source edits and GitHub compare: DONE. The final post-status SHA is
in the latest worker comment on the same Draft PR #5.
SDK restore/build: **NOT RUN** on this new source.
TestServer bootstrap: **NOT RUN**.
Eight HTTP scenarios: **NOT RUN**, all outcomes unknown.
Owner must run exact-HEAD local validation, capture the complete
stdout/stderr, case totals and exit code, and submit for orchestrator
review. An expected possible client-ID policy failure is not to be
hidden or waived.

Owner command, after updating/cleaning both pinned Git checkouts:

    Set-Location D:\Projects\TradeOps-v2
    & .\scripts\to-v2-03-validate-local.ps1 -ExpectedTradeOpsHead '<latest PR #5 head>'
    $code = $LASTEXITCODE
    if ($code -ne 0) { throw "TO-V2-03 VALIDATION FAILED" }

---

## 2026-10-10 — owner Windows compile CS1674 follow-up (prior compile failure)

**Status: SOURCE FIX APPLIED / OWNER RERUN NEEDED / 8 HTTP SCENARIOS NOT RUN / DRAFT PR #5 — NO MERGE.**
This section supersedes older owner evidence snapshots below. The exact final
post-STATUS commit HEAD is recorded in PR #5 because the document cannot
truthfully contain the SHA of its own writing commit.

**Actual owner Windows test on TradeOps head
`0f9f4d1a3c95d3129d173dc682fee1b467d917f3`, DocFlow-v2 read-only
main `3f09e91ba356fb367248ef1b48a56b19d9fdae97`, SDK 10.0.401:**

| Check | Owner result on previous head |
|---|---|
| Branch/main/clean checkout and SDK preflight | PASS |
| Temporary cross-repo NuGet restore | **PASS** (3.0s) |
| Real DocFlow and TradeOps dependent project Release builds | **PASS** (net10.0) |
| DualHttpContract harness Release compilation | **FAIL: 2 × CS1674** |
| First error | Program.cs(187,9): test-only DeterministicFactory used with `using` but does not implement IDisposable |
| Second error | Program.cs(222,9): same test-only DeterministicFactory used with `using` but does not implement IDisposable |
| All eight TestServer integration scenarios | **NOT RUN** (harness did not compile) |
| Validator exit code | **1 (FAIL)** |

The additional `Write-Host "TO-V2-03 LOCAL VALIDATION PASSED"` displayed in
owner's transcript was a **separate manually entered command following a
PowerShell throw**; it does not overturn the actual build failure or exit 1.

**Worker corrective change (test-only source):** In
`tools/DualHttpContract/Program.cs`, replaced both `using var directFactory`
declarations with ordinary `var directFactory` local variables. The
`DeterministicFactory` owns no disposable resources and is not modified.
Remaining `using var` targets were inspected by source; they are test hosts,
HTTP clients/messages, JsonDocument and CancellationTokenSource instances.
The eight cases, real production adapter/facade, and strict negative JWT
assertion for `client_id="unapproved-service-client"` remain unchanged.
No changes to production TradeOps, read-only DocFlow-v2, GitHub Actions,
workflows, private NuGet, broker/provider networks, or merger state.

**Corrected new-head execution evidence:** NuGet restore **NOT RUN**; net10
Release compilation **NOT RUN**; eight HTTP scenarios **NOT RUN**, with
unknown passed/failed/assertion totals. Prior successful dependency builds
do not prove new-head acceptance. Independent Development Orchestrator
review remains required, especially for the existing upstream identity
allowlist issue. See latest PR #5 worker comment for the new exact HEAD and
full PowerShell repeat command.

---

## 2026-10-10 — owner Windows compile failure and worker syntax correction (prior CS1026)

**Current state: HARNESS SOURCE FIXED; NEW-HEAD BUILD / ALL EIGHT HTTP
SCENARIOS NOT RUN; PR #5 STILL DRAFT, MERGE BLOCKED.** This update
supersedes the older "no owner machine evidence" statements below.
The final corrected branch HEAD is recorded in the latest PR #5 worker
comment after this documentation commit, to avoid self-referential SHA.

**Verified owner evidence on OLD TradeOps HEAD
`fd6cd4026e9d8222c09b84ce19c483b0c36a1cd0`** against read-only
DocFlow-v2 `3f09e91ba356fb367248ef1b48a56b19d9fdae97`:

| Operation on owner Windows 10 / SDK 10.0.401 | Actual result |
|---|---|
| Clean branch / pinned checkout preflight | PASS |
| Temporary source-linked NuGet restore | **PASS** (owner reports 5.0 s) |
| Real TradeOps + DocFlow dependent net10 project builds | **PASS** |
| Test harness Program.cs C# compilation | **FAIL** `CS1026: ) expected`, line 286 column 77 |
| Eight HTTP scenarios | **NOT RUN** (build prevented execution) |
| Wrapper exit code | **1** |

Development Orchestrator diagnosed a missing close-parenthesis around the
nested `DocFlowHttpExtractionAdapter.ExtractAsync(Input(...))` call in
`RequestContractAndBounds`; see the latest owner comment in PR #5.

**Worker correction in existing assigned branch only:**
The incorrect nested expression

```csharp
await Sanitized(() => Adapter(client).ExtractAsync(Input(
    raw: Json("""{"invalid":"document"}""")), "normalization error");
```

was changed to

```csharp
await Sanitized(
    () => Adapter(client).ExtractAsync(
        Input(raw: Json("""{"invalid":"document"}"""))),
    "normalization error");
```

The entire corrected `Program.cs` was inspected, and a lexical
delimiter scan, ignoring comments / raw, quoted and character literals,
found **zero unmatched parentheses, braces, or brackets**. This is
*source-level only*, not compiler certification. The eight registered
test cases, deterministic fake backend, production source references and
the explicit `client_id="unapproved-service-client"` negative
JWT requirement are unchanged. No production TradeOps source was modified;
DocFlow-v2, Actions, workflows, NuGet publish and merge were untouched.

**NEW HEAD evidence:** restore **NOT RUN**, Release build **NOT RUN**,
all eight HTTP scenarios **NOT RUN** (passed/failed/assertion totals
unknown), and SDK validation at corrected HEAD **NOT RUN**. The prior
successful dependency builds do **not** imply a successful new-head
harness build or integration test pass. The intentionally strict JWT
client-id rejection may still fail against the unchanged DocFlow.Api
policy, as documented in the contract matrix; do not weaken it.

**Owner exact-head rerun:** with both private checkouts clean, refresh
remote refs, keep TradeOps on the *existing* TO-V2-03 branch and
DocFlow-v2 on pinned main; run

```powershell
Set-Location D:\Projects\TradeOps-v2
& .\scripts\to-v2-03-validate-local.ps1 -ExpectedTradeOpsHead '<NEW_PR5_HEAD>'
$LASTEXITCODE
```

Replace placeholder using the final SHA in PR #5 latest worker comment.
Do not bypass the SHA, clean-working-tree, 15-project, or SDK guards.
Collect all eight per-case PASS/FAIL lines, totals, actual stdout/stderr
and exit code. Orchestrator reviews results before any merge.

---

## Exact repositories and reviewed sources

- TradeOps-v2 PRIVATE owner: avysotsky/TradeOps-v2.
- Assigned existing branch: TradeOps/to-v2-03-dotnet10-dual-http-contract.
- Registered initial assignment HEAD: 6aac07796f22ac8fdb04c7333d87125f0a49969c.
- Reviewed TradeOps-v2 main: bab7b5c48bbb30677f7747fe2445c2673fd663c5.
- Read-only DocFlow-v2 main: 3f09e91ba356fb367248ef1b48a56b19d9fdae97.
- Current branch final HEAD: consult live PR #5, recorded in the final worker comment
  after the status commit. Avoid circularity from putting the status commit SHA
  into its own content.
- Preflight compare: assignment branch was 1 commit ahead of main / 0 behind,
  containing only the assignment. Preflight PR #5 was open Draft.
- Preflight accessible GitHub Actions status / recent workflow runs on the
  registered head: **none**. Historical green CI of other commits is not
  evidence for this source. GitHub Actions remain **OFF** and were not invoked.
- No writes to DocFlow-v2, existing TradeOps main, archive/public repositories,
  workflows, packages or trading code.

## Delivered files and source graph

1. tools/DualHttpContract/Program.cs — eight cases / counted assertions.
   Uses real TradeOps.Application port and real TradeOps.Infrastructure
   DocFlowHttpExtractionAdapter, real in-memory DocFlow.Api endpoint
   with JWT validation, real DocFlowExtractionFacade, TextDocumentNormalizer,
   NativeExtractionService and schema validation.
2. scripts/to-v2-03-validate-local.ps1 — exact source SHA/branch/clean-tree
   preflight, SDK 10.0.401 / net10.0 checks, fixed 15-project inventory,
   ephemeral source-linked .NET 10 project (NOT checked into source), fixed
   public NuGet.org-only restore, Release build and TestServer execution.
   Nonzero exit for restore/build/test/guard failure; scratch project deleted.
3. docs/orchestration/workstreams/TO_V2_03_CONTRACT_MATRIX.md — per-case
   expected coverage versus actual execution, edge-only test delineation.
4. This status and existing Draft PR #5 update.

Source-only external dependencies are provided as project references at run time:

    temporary DualHttpContract.csproj (net10.0)
      -> D:\Projects\TradeOps-v2\src\TradeOps.Infrastructure\TradeOps.Infrastructure.csproj
           -> TradeOps.Application (IDocFlowExtractionPort)
      -> D:\Projects\DocFlow-v2\src\DocFlow.Api\DocFlow.Api.csproj
           -> DocFlow.Extraction.Application, Normalization, SchemaExtraction

No new csproj was committed, so TradeOps.sln and the existing TO-V2-02
15-project validation inventory are structurally unchanged.
No DocFlow source files are copied or modified; private NuGet packages are
not published or consumed.

## Safety and fixture review

- WebApplicationFactory + ASP.NET Core TestServer; HTTP request scheme HTTPS,
  localhost-only in-memory HttpClient, no real Kestrel listener, DNS or sockets.
- Real JwtBearer validation with a test-only signing key and static issuer
  configuration. Issuer is an invalid TLD; metadata discovery disabled.
  Explicit service scope and client_id requirements are preserved.
- Injects only test IExtractionBackendFactory and per-request deterministic
  IRequestAwareExtractionBackend / IConfidentExtractionBackend.
- DbContext and its options registrations are removed inside test host.
  Fake unreachable local connection string is a secondary defense; no
  EF queries or migrations should execute.
- Production LocalFileStorage constructor is confined to unique directory
  under the current user's temporary filesystem; tests assert no files,
  host cleanup removes the directory.
- No real TradeOps Api/Worker startup, external LLM, trading orders or
  cancellations, brokerage/testnet access, OIDC, production database, static
  tokens from any connected service, live documents, or logging of payloads.
- Edge-only HTTP response malicious fixtures remain explicitly separate
  from tests using native DocFlow.Api + facade; neither production adapter
  nor production facade is mocked.
- No Actions, workflow YAML, private feed, NuGet publish, merge or in-process
  adapter modification performed.

## Actual worker test execution

| Operation | Result |
|---|---|
| GitHub read of exact source, assignment, adapter, endpoint, facade and native API tests | DONE |
| GitHub code and document commits in existing assigned branch | DONE |
| GitHub Actions | OFF / NOT RUN |
| .NET 10.0.401 SDK verification on owner Windows machine | NOT RUN |
| Temporary project restore and compilation | NOT RUN |
| Eight native HTTP contract scenarios | NOT RUN (0 results, not 0 failures) |
| Passing assertions or runtime success | **NOT ESTABLISHED** |
| Real provider/identity/TLS/DB/broker/network evidence | NOT IN SCOPE / NOT RUN |
| TradeOps.sln full build and prior 15-project gate at this new HEAD | NOT RUN |

Reason: worker execution environment does not have a dotnet executable or the
owner's private D:\Projects checkout; GitHub connector edits do not execute C#.
No passing counts or clean build were invented. Treat PR as **review required**,
not as merged or acceptance proven.

## Owner-local reproduction, exact-HEAD gate

On Windows 10 with Visual Studio 2026 and D:\DotNet\dotnet.exe SDK 10.0.401,
refresh both Git remote pointers, check out the existing assignment branch
in TradeOps-v2 and pinned main in DocFlow-v2, ensuring both trees clean.
Example from PowerShell:

    git -C D:\Projects\TradeOps-v2 fetch origin
    git -C D:\Projects\DocFlow-v2 fetch origin
    git -C D:\Projects\TradeOps-v2 switch TradeOps/to-v2-03-dotnet10-dual-http-contract
    git -C D:\Projects\TradeOps-v2 pull --ff-only origin TradeOps/to-v2-03-dotnet10-dual-http-contract
    git -C D:\Projects\DocFlow-v2 switch main
    git -C D:\Projects\DocFlow-v2 pull --ff-only origin main
    Set-Location D:\Projects\TradeOps-v2
    git rev-parse HEAD
    & .\scripts\to-v2-03-validate-local.ps1 -ExpectedTradeOpsHead '<PR #5 final SHA>'

Substitute the literal 40-hex PR HEAD reported in the worker's final
comment, with no angle brackets. If either main source SHA changes, do not
bypass guards; request orchestrator review / re-pin. The validator prints
per-case PASS/FAIL, a total for 8 scenarios and an explicit exit code.
Attach console output and full commit information to PR #5 for independent
review. Some restore traffic to public NuGet.org is permitted; no actual
service/LLM/production DB/broker calls are permitted.

## Blocking issue revealed by source review

DocFlow.Api's real policy only requires a nonempty client_id/azp/appid and
the correct scope, not an exact/approved TradeOps service identity. The native
JWT fixture now asserts that a correctly signed, properly scoped token with
an **unapproved nonempty client_id** is rejected with 403. The unmodified
read-only DocFlow.Api implementation appears to return 200. Thus this
assertion is **EXPECTED TO FAIL** when executed; that assessment comes from
code review and is not a fabricated runtime result. Do not drop the assertion
or inject an artificially stricter test policy to force green. Escalate the
contract/authentication requirement to Development Orchestrator for a
separate approved DocFlow-v2 change, or a documented security decision.
The worker must not modify DocFlow-v2.

## Open acceptance and review decisions

- Owner must run actual Windows SDK 10.0.401 Release restore/build/TestServer
  smoke at exact pinned commits, report case counts and failures.
- Orchestrator must independently inspect test cases and auth/DB protections,
  confirm no runtime regressions or leak, and explicitly review any SHA
  movement/new baseline. HTTP transport code itself was unchanged.
- Required production service-token issuance, network TLS topology, external
  provider, package licensing/ACL and provider-independent deployments are
  separate gated workstreams and not accepted by this source-only PR.
- Preserve historical MIG-02 Python-v1 evidence separately; it is not native
  .NET10 verification. **No worker merge**.
