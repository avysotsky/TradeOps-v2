# TO-V2-04 — Current-main .NET 10 dual-HTTP qualification status

**WORKER IMPLEMENTATION COMPLETE. OWNER WINDOWS EXACT-CODE ACCEPTANCE: PASS. PENDING INDEPENDENT DEVELOPMENT ORCHESTRATOR REVIEW. PR #6 OPEN / DRAFT; WORKER NO MERGE.**

## Latest owner Windows acceptance — 2026-10-10

Owner supplied full PowerShell output of the NEW TO-V2-04 script, launched from D:\Projects\TradeOps-v2. The source pair and execution were verified by the script, and the log ended with both native and wrapper success summaries followed by exit 0.

| Exact source / gate | Owner-observed result |
|---|---|
| TradeOps-v2 **CODE SHA EXECUTED** | a688b13839c99c0d3d7fe2cea3e8f8651ace4d02 |
| TradeOps-v2 approved main / origin/main | 7fab8a4e862c7fbdc2d717941a189308cd69fc18 |
| DocFlow-v2 exact clean main / origin/main | 22741d73a67112eae929d65337818b9b53629c8e |
| Required branch, origin tracking, clean trees, exact HEAD / baseline pins | PASS (validator reached/cleared all preflight and postflight gates) |
| Both global.json SDK declarations and actual SDK | 10.0.401 / PASS |
| TradeOps projects | 15 enumerated, all net10.0; MigrationSmoke included despite absence from solution |
| 15 project restores, -warnaserror | **15/15 PASS** |
| 15 project Release builds, -warnaserror --no-incremental | **15/15 PASS** |
| Disposable source-linked real TradeOps + DocFlow harness restore | PASS |
| Real TradeOps + DocFlow dependent net10 Release build | PASS; **0 Warning(s), 0 Error(s)** |
| In-memory native DocFlow.Api TestServer bootstrap | PASS; real /health/live HTTP 200 |
| Eight HTTP contract scenarios | **8/8 PASS; 0 FAIL** |
| Counted HTTP assertions | **171** |
| Existing underlying harness summary | TO-V2-03 TOTAL cases=8 passed=8 failed=0 assertions=171 |
| New source-exact wrapper summary | TO-V2-04 TOTAL cases=8 passed=8 failed=0 assertions=171 |
| Final PowerShell exit | **0** |
| GitHub Actions | OFF; NOT RUN |

Eight owner-verified per-case PASS results:

1. canonical-native-parity-utf8
2. invalid-provider-data
3. jwt-security-https
4. request-contract-and-bounds
5. http-response-and-bounds
6. backend-errors-and-routing
7. deadline-and-cancellation
8. bounded-concurrency

The real native JWT configuration is loaded early in the TestServer. The owner log shows an allowlisted correctly signed identity reaching the actual extraction route with HTTP 200 and a correctly signed / scoped disallowed client returning HTTP 403. Unchanged harness assertions additionally verified **no extra backend dispatch** after rejection. This is native DocFlow.Api authorization, not a policy stub.

The owner log also shows native deadline HTTP 504, cancellation HTTP 499, bounded concurrency HTTP 429, rejected oversized bodies HTTP 413, valid exact-limit requests HTTP 200 and other required failures, with all scenario assertions PASS. The literal HTTP requests to localhost in output are **in-memory TestServer**; no public socket or deployed host was used.

## Tested code SHA versus later documentation commits

**Do not relabel the owner-tested code SHA.** Exact executed TradeOps branch HEAD was a688b13839c99c0d3d7fe2cea3e8f8651ace4d02. This status and the accompanying validation-notes evidence update are subsequent **docs-only** commits and were not part of the owner-run HEAD. Development Orchestrator must confirm live PR diff contains no implementation changes after the tested commit before accepting this evidence. If any validator, Program.cs, project, production or dependency source changes, rerun required on the new exact source graph. Merely writing evidence documents does not generate another test pass.

The final evidence-document HEAD is tracked in the live PR #6 and the worker handoff comment, not embedded self-referentially in this file.

## Source baselines and implementation

- Private TradeOps-v2 approved main remained 7fab8a4e862c7fbdc2d717941a189308cd69fc18 during preflight and latest GitHub reread.
- Private DocFlow-v2 main remained 22741d73a67112eae929d65337818b9b53629c8e.
- Existing branch: TradeOps/to-v2-04-current-main-http-qualification. Initial assignment HEAD: fd04b411b5b726afe0e6b7db0c9ca839a3317ba6.
- Existing PR #6 remains Draft. No other open PRs observed at initial preflight.
- scripts/to-v2-03-validate-local.ps1: historical defaults and underlying validation engine preserved; optional branch/main/dependency pins added.
- scripts/to-v2-04-validate-local.ps1: exact branch/origin/clean source gate, all-15 net10.0 restore/build, child source-linked native TestServer run and strict one-total/eight-case/171-assertion result gate.
- docs/orchestration/workstreams/TO_V2_04_VALIDATION_NOTES.md: owner-run command, evidence and retained boundaries.
- This TO_V2_04_STATUS.md: exact owner evidence and distinction between source code and later documentation edits.
- Original docs/orchestration/workstreams/TO_V2_04_ASSIGNMENT.md: registered assignment file, not altered by worker.

TradeOps.sln contains 14 projects but tools/MigrationSmoke/TradeOps.MigrationSmoke.csproj is the fifteenth. All 15 were individually restored and built.

## Historical evidence remains separate

| Proof | Historical TO-V2-03 | NEW TO-V2-04 owner Windows |
|---|---|---|
| TradeOps executable code | 84fa7eaa080800b74f28cde50d1659a3eb3fbab0 | a688b13839c99c0d3d7fe2cea3e8f8651ace4d02 |
| DocFlow main | 7e9dc58947797251ab3be188411f514a6b078ac3 | 22741d73a67112eae929d65337818b9b53629c8e |
| SDK | 10.0.401 PASS | 10.0.401 PASS |
| Source-linked restore/build | PASS | PASS |
| Native HTTP TestServer | 8/8 PASS | 8/8 PASS |
| Assertions | 171 | 171 |
| Exit | 0 | 0 |

These are **two separate owner runs against different source pairs**, not a duplicated or transferred result. Neither constitutes exhaustive production security review, live broker/LLM/provider integration testing, full unit-test execution or NuGet distribution approval.

## Boundaries and next reviewer action

The harness exercises genuine TradeOps.Infrastructure.DocFlowHttpExtractionAdapter, native DocFlow.Api endpoint and DocFlow.Extraction.Application.DocFlowExtractionFacade; fake dependencies are limited to test JWT issuer and external provider/backend, filesystem/storage and DB isolation. No production src/, tools/DualHttpContract/Program.cs, DocFlow-v2 code, workflows, public listeners or trading endpoints were changed.

**GitHub Actions OFF; NuGet PUBLISH BLOCKED; LICENSE APPROVAL BLOCKED.** No workflow YAML edits, package publish, external OIDC/LLM, production DB, real broker API, in-process NuGet adapter, public rollout or worker merge.

Next: Development Orchestrator independently review PR #6, compare post-tested documentation-only changes with owner-executed HEAD, inspect the strict JWT 403-before-backend and eight native cases, then decide whether the proof meets the merge gate. Worker explicitly leaves PR #6 OPEN / DRAFT.
