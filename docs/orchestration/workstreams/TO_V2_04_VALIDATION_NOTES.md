# TO-V2-04 — Exact-head offline Windows validation notes

## OWNER WINDOWS ACCEPTANCE EXECUTED — PASS, 2026-10-10

The owner executed this runbook on Windows at the following **exact source code**. All resulting PASS counts below come from the supplied console log, not from an earlier workstream.

| Source / gate | Actual observation |
|---|---|
| TradeOps-v2 worker code HEAD tested | a688b13839c99c0d3d7fe2cea3e8f8651ace4d02 |
| TradeOps-v2 origin/main pinned | 7fab8a4e862c7fbdc2d717941a189308cd69fc18 |
| DocFlow-v2 clean main pinned | 22741d73a67112eae929d65337818b9b53629c8e |
| SDK | 10.0.401 PASS |
| Exact branch, upstream refs, origins, clean trees, 15 net10 projects | PASS (validator passed both source gates) |
| Explicit 15 TradeOps project restores | **15/15 PASS** |
| Explicit 15 TradeOps project Release builds | **15/15 PASS** with -warnaserror |
| Temporary source-linked TradeOps/DocFlow restore/build | PASS, 0 warnings, 0 errors in harness build |
| Native DocFlow.Api in-memory TestServer /health/live | HTTP 200 PASS |
| canonical-native-parity-utf8 | PASS |
| invalid-provider-data | PASS |
| jwt-security-https | PASS |
| request-contract-and-bounds | PASS |
| http-response-and-bounds | PASS |
| backend-errors-and-routing | PASS |
| deadline-and-cancellation | PASS |
| bounded-concurrency | PASS |
| Genuine TestServer total | **cases=8 passed=8 failed=0 assertions=171** |
| Wrapper TO-V2-04 total | **cases=8 passed=8 failed=0 assertions=171** |
| Actual process exit | **0** |
| GitHub Actions | OFF; NOT RUN |

**Security-critical detail:** native JWT authorization completed with the signed/allowlisted integration identity yielding HTTP 200; the separate validly signed, properly scoped but **unapproved** service identity yielded HTTP 403. Unchanged harness asserts that the latter rejection occurs **before another backend is created**. Source config uses real early-loaded native DocFlow allowlist from isolated test JSON, not substituted authorization middleware.

**Scope detail:** eight original scenarios remain one in-memory source-linked harness execution, not sixteen independent tests; the two TOTAL lines are separate report layers around the same eight cases. Fake external provider and local synthetic JWT issuance are allowed; no real network-bound HTTP listeners, OIDC/LLM/brokers or production DB were contacted by the harness. Public NuGet.org access was used for dependency restores only.

**Evidence boundary:** successful owner-run TradeOps code HEAD is exactly a688b13839c99c0d3d7fe2cea3e8f8651ace4d02. Later changes to THIS NOTE and TO_V2_04_STATUS.md are **documentation only**, and their resulting higher branch HEAD has not itself been re-executed. Development Orchestrator must confirm the diff from the tested code SHA contains only those evidence-document updates and must not transplant the PASS to any subsequent code change. If implementation or dependency source changes, repeat all gates at the new exact HEAD.

The original pre-run instructions below remain valid as a recipe for an independent repeat, with the expected TradeOps HEAD changed to the latest live PR HEAD if performing a NEW run. The old NOT RUN ledger is superseded by this owner evidence.

## Scope / immutability

Target is a NEW independent source-linked regression run of **TradeOps-v2 worker branch** against the two approved PRIVATE main baselines:

| Repository ref | Approved exact SHA |
|---|---|
| avysotsky/TradeOps-v2 origin/main | 7fab8a4e862c7fbdc2d717941a189308cd69fc18 |
| avysotsky/DocFlow-v2 origin/main and clean local main HEAD | 22741d73a67112eae929d65337818b9b53629c8e |
| TradeOps worker | TradeOps/to-v2-04-current-main-http-qualification, at the live exact HEAD of Draft PR #6 |

Initial registered worker HEAD: fd04b411b5b726afe0e6b7db0c9ca839a3317ba6 (assignment-only; **NOT** the completed worker HEAD). Always copy the latest full 40-hex HEAD from PR #6 after the last worker commit. Do not edit baseline pins or choose a different branch merely to make validation pass.

## Owner instructions — Windows PowerShell, local machine

Prerequisites: genuine private checkouts at D:\Projects\TradeOps-v2 and D:\Projects\DocFlow-v2, Git access to their private origin repositories, installed D:\DotNet\dotnet.exe SDK 10.0.401. NuGet.org package restore is expressly permitted; no other external API access is used or authorized.

1. Confirm each current working tree has no tracked or untracked modifications. Stop if dirty; do not discard owner work to satisfy the gate.
2. Refresh both origin refs. Switch only to the ALREADY EXISTING worker branch in TradeOps and main in DocFlow; pull fast-forward only. Stop if changed upstream main SHAs are reported.
3. Re-verify the live GitHub PR #6 HEAD matches the literal 40-hex value supplied to the script. Never derive acceptance from the old assignment-only HEAD.
4. Run the existing TO-V2-04 validation script, capture all STDOUT/STDERR, and retain the actual process exit code.

PowerShell setup commands (read-only with respect to remote branches):

    $ErrorActionPreference = 'Stop'
    $to = 'D:\Projects\TradeOps-v2'
    $df = 'D:\Projects\DocFlow-v2'
    foreach ($repo in @($to,$df)) {
        if (git -C $repo status --porcelain --untracked-files=normal) {
            throw "Working tree is dirty: $repo"
        }
        git -C $repo fetch origin
        if ($LASTEXITCODE -ne 0) { throw "Fetch failed: $repo" }
    }
    git -C $to switch 'TradeOps/to-v2-04-current-main-http-qualification'
    if ($LASTEXITCODE -ne 0) { throw 'TradeOps switch failed' }
    git -C $to pull --ff-only origin 'TradeOps/to-v2-04-current-main-http-qualification'
    if ($LASTEXITCODE -ne 0) { throw 'TradeOps pull failed' }
    git -C $df switch main
    if ($LASTEXITCODE -ne 0) { throw 'DocFlow switch failed' }
    git -C $df pull --ff-only origin main
    if ($LASTEXITCODE -ne 0) { throw 'DocFlow pull failed' }

Actual validator execution: replace the quoted placeholder with the current FULL 40-hex PR #6 HEAD read on GitHub:

    Set-Location 'D:\Projects\TradeOps-v2'
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File '.\scripts\to-v2-04-validate-local.ps1' -ExpectedTradeOpsHead '<LATEST_FULL_PR6_HEAD>'
    $code = $LASTEXITCODE
    Write-Host "TO-V2-04 exit code: $code"
    if ($code -ne 0) { throw 'TO-V2-04 acceptance FAILED' }

The validator accepts optional TradeOpsRoot, DocFlowRoot and DotNetExe paths, but never silently changes SHA pins or the required branch. It enforces the actual upstream mapping to origin/branch and origin/main; merely passing a caller-provided HEAD cannot bypass Git preflight. Origin URLs must point to the approved GitHub repositories (standard HTTPS or git@github.com SSH forms). Both trees must be clean before and after.

## Preflight and compilation gate

- Check pinned remote origin URLs, branch names, upstream tracking ref names, both exact HEADs, exact origin branch pointer, exact origin/main pointers, and clean tracked/untracked trees.
- Reject inherited ASPNETCORE_URLS / ASPNETCORE_HTTP_PORTS / HTTP_PORTS / DOTNET_URLS listener binding settings before any build/test.
- Verify real SDK reported by D:\DotNet\dotnet.exe --version is 10.0.401 and BOTH global.json version declarations match.
- Inventory src/, tests/ and tools/: exactly **15 distinct TradeOps .csproj** files; each must declare TargetFramework net10.0. Ensure the test harness Program.cs exists and there is no checked-in sixteenth harness .csproj.
- Restore all 15 TradeOps projects from approved public NuGet.org with -warnaserror. Build **each of the 15** in Release using --no-restore, --no-incremental and -warnaserror. This includes the MigrationSmoke .csproj which is absent from TradeOps.sln.
- Invoke historical scripts/to-v2-03-validate-local.ps1 through a separate local PowerShell process with **explicit** TO-V2-04 expected branch, TradeOps main and DocFlow main parameters. The original script repeats the exact SHA and clean-tree checks, verifies the 15-project net10 inventory, creates an ephemeral source-linked harness .csproj, executes genuine restore/Release build and the native in-memory TestServer program.
- Enforce child process exit 0, eight unique per-case PASS lines, exactly one aggregate with cases=8 passed=8 failed=0 and at least 171 counted assertions. A bootstrap_failed summary, any missing case, an old-source pin, an unexpected count, or any native restore/build/test nonzero exit is a **failure**, never PASS.
- Verify source HEADs and clean tracked/untracked trees again after the tests. On success, display a separate TO-V2-04 TOTAL and exact-source PASS.

The underlying eight-case harness still deliberately prints TO-V2-03 case lines and aggregate because this worker does not edit or duplicate Program.cs. The final wrapper also prints an independent TO-V2-04 TOTAL. Do not count the same eight tests twice; they are one genuine invocation.

## Eight native scenarios and trust boundaries

| Scenario | Real objects and required behavior |
|---|---|
| canonical-native-parity-utf8 | Real TradeOps HTTP adapter -> genuine DocFlow.Api TestServer -> actual DocFlowExtractionFacade; deep JSON parity, Unicode, stable IDs/fingerprint, provenance |
| invalid-provider-data | Native facade reports schema-invalid synthetic backend output, no result laundering |
| jwt-security-https | Native JWT validation and native server-owned client allowlist; correctly signed allowlisted client succeeds (HTTP 200); correctly signed/scope-valid but disallowed client receives **HTTP 403 before backend creation** |
| request-contract-and-bounds | Real route validation, Unicode and invalid UTF-8, duplicate keys, content limits and schema bounds |
| http-response-and-bounds | Genuine adapter enforces received-response limits; native server produces its own output bounds |
| backend-errors-and-routing | Genuine route/provider failure mapping, no retries or provider sentinel leakage |
| deadline-and-cancellation | Server deadline HTTP 504, real backend cancellation, safe client failures without retries |
| bounded-concurrency | Native semaphore/HTTP 429 before second backend dispatch |

**No production authorization bypass.** WebApplicationFactory and in-memory TestServer use real DocFlow.Api and route. The configured test client allowlist is early-loaded through separate temporary JSON appsettings content roots, as established in DF-V2-08. Test JWTs are locally signed by the source-linked fixture. The only mocked backend is the external provider edge. The real TradeOps.Infrastructure DocFlowHttpExtractionAdapter and native DocFlow.Extraction.Application facade are never mocked.

No Kestrel bound sockets; no real OIDC metadata fetch, external LLM, trading broker API, production database, production storage endpoint, NuGet publish, public web deployment or in-process NuGet adapter. Fixture storage uses a disposable sandbox. Approved public NuGet.org is allowed ONLY for dependency restore, not publication.

## Verified evidence ledger — old/new source pairs

| Evidence | Historical TO-V2-03 | New TO-V2-04 owner Windows |
|---|---|---|
| TradeOps tested code SHA | 84fa7eaa080800b74f28cde50d1659a3eb3fbab0 | a688b13839c99c0d3d7fe2cea3e8f8651ace4d02 |
| DocFlow tested main SHA | 7e9dc58947797251ab3be188411f514a6b078ac3 | 22741d73a67112eae929d65337818b9b53629c8e |
| .NET SDK 10.0.401 | PASS | PASS |
| Restore/build | PASS | 15/15 projects PASS; source-linked harness PASS |
| Native TestServer / real adapter and facade | PASS | PASS |
| JWT signed allowlisted 200 / disallowed signed 403 before backend | PASS | PASS |
| Eight HTTP cases | 8/8 PASS | **8/8 PASS** |
| Assertions | 171 | **171** |
| Exit | 0 | **0** |
| GitHub Actions | OFF | OFF / NOT RUN |

These correspond to two separate source pairs and two separate owner executions. Do not interpret a docs-only evidence commit as another run, or infer public-release readiness.

## Owner evidence to provide to Development Orchestrator

Capture complete command output, literal -ExpectedTradeOpsHead, local/remote TradeOps HEAD, local/remote DocFlow main SHA, actual SDK version, completion and warnings for all 15 project restores/builds, all eight individual PASS/FAIL lines, raw single TO-V2-03 TOTAL, wrapper TO-V2-04 TOTAL, real process exit code. Record environmental failures separately from product contract failures.

This worker leaves PR #6 OPEN/DRAFT and does not merge. Only Development Orchestrator may review the independently executed acceptance gate and decide whether to integrate. **GitHub Actions OFF; NuGet PUBLISH BLOCKED; LICENSE APPROVAL BLOCKED.**
