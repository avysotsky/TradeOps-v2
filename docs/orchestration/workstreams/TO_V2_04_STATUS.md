# TO-V2-04 — Current-main .NET 10 dual-HTTP qualification status

**Worker delivery: implementation committed in existing branch; independent owner Windows acceptance PENDING; Draft PR #6; NO MERGE.**

## Source of truth at assignment start — 2026-10-10

| Subject | Verified GitHub state |
|---|---|
| TradeOps-v2 PRIVATE main | 7fab8a4e862c7fbdc2d717941a189308cd69fc18 |
| DocFlow-v2 PRIVATE main | 22741d73a67112eae929d65337818b9b53629c8e |
| Existing worker branch | TradeOps/to-v2-04-current-main-http-qualification |
| Registered initial worker HEAD | fd04b411b5b726afe0e6b7db0c9ca839a3317ba6 |
| Registered compare | 1 ahead / 0 behind; assignment only |
| Workstream PR | TradeOps-v2 #6 OPEN / DRAFT |
| Other open PRs | None observed in either private repo at preflight |
| Orchestrator instruction | PR #6 issue comment 6100000474 read in full |
| Workstream registry | Historical snapshot; live main and PR refs take precedence |

Both expected main SHAs matched GitHub before any worker code changes; no baseline-change escalation was required.

## Delivered changes

1. **scripts/to-v2-03-validate-local.ps1:** add optional expected branch, TradeOps main and DocFlow main parameters, with original TO-V2-03 defaults unchanged. The pre-existing exact-source checks, 15-project inventory guard, temporary source links, restore/build, real TestServer implementation and output remain unchanged.
2. **scripts/to-v2-04-validate-local.ps1:** new Windows entry point. Checks GitHub origin, exact branch and upstream/origin tracking, two reviewed main refs, passed-in exact worker HEAD, both clean trees, SDK and global.json 10.0.401, all 15 net10.0 TradeOps projects. Restores and builds all 15 projects in Release with warnings-as-errors, then calls the original eight-case TestServer harness with explicitly pinned current-main source refs. Requires eight unique expected PASS lines, one complete 8/8 total and at least 171 counted assertions.
3. **docs/orchestration/workstreams/TO_V2_04_VALIDATION_NOTES.md:** exact-head owner runbook and fail-closed acceptance ledger.
4. This status file.
5. Existing Draft PR #6 body and handoff comment to be updated; no new branch or PR.

**Inventory detail:** TradeOps.sln contains 14 of the 15 existing .csproj files. tools/MigrationSmoke/TradeOps.MigrationSmoke.csproj is not included. The new script enumerates all 15 rather than assuming a solution build covers everything.

## Historical results versus new results

| Phase | Historical TO-V2-03 owner proof | New TO-V2-04 current-main |
|---|---|---|
| TradeOps SHA actually tested | 84fa7eaa080800b74f28cde50d1659a3eb3fbab0 | NOT RUN (must pass new PR HEAD to validator) |
| DocFlow SHA actually tested | 7e9dc58947797251ab3be188411f514a6b078ac3 | Target 22741d73a67112eae929d65337818b9b53629c8e; NOT RUN |
| Windows SDK 10.0.401 | PASS on historical pair | **NOT RUN** |
| Source-linked NuGet restore | PASS on historical pair | **NOT RUN** |
| Release build warnings-as-errors | PASS on historical pair | **NOT RUN** |
| Native DocFlow.Api/TestServer bootstrap | PASS on historical pair | **NOT RUN** |
| Eight genuine HTTP scenarios | **8/8 PASS** on historical pair | **NOT RUN** |
| Counted HTTP assertions | **171** on historical pair | **NOT RUN / UNKNOWN** |
| Validator exit code | **0** on historical pair | **NOT RUN / UNKNOWN** |
| GitHub Actions | OFF | OFF / NOT RUN |

Historical TO-V2-03 evidence was subsequently integrated. It is **not** proof for current DocFlow main, nor for the new branch.

## Eight scenarios preserved, unchanged source

- canonical-native-parity-utf8
- invalid-provider-data
- jwt-security-https
- request-contract-and-bounds
- http-response-and-bounds
- backend-errors-and-routing
- deadline-and-cancellation
- bounded-concurrency

The genuine tools/DualHttpContract/Program.cs still requires a signed allowlisted tradeops-integration-test client to succeed (native HTTP 200) and a correctly signed, properly scoped unapproved-service-client to receive **HTTP 403 before backend dispatch**. The native DocFlow.Api, DocFlowExtractionFacade and real TradeOps DocFlowHttpExtractionAdapter are exercised. Only external provider/backend, JWT signing and storage/database edges use isolated synthetic fixtures; the native authorization policy is not mocked or bypassed.

## Blockers and handoff

**Implementation reviewed at GitHub source level only.** PowerShell parsing, SDK, 15-project restore/build, cross-repo TestServer bootstrap, eight scenario outcomes, assertions and process exit must be independently executed on the owner's Windows machine. Record **NOT RUN** until that happens.

No production src/, tools/DualHttpContract/Program.cs, DocFlow-v2 source, workflows, private NuGet artifacts, package ACLs, public listeners, external identity/model/broker/production DB services changed or ran. GitHub Actions OFF. **NuGet PUBLISH BLOCKED; LICENSE APPROVAL BLOCKED; worker NO MERGE.**

Owner runs the instructions in TO_V2_04_VALIDATION_NOTES.md and provides complete per-phase console evidence. Development Orchestrator must review exact refs, source diff, new 8/8 and actual assertion count before any merge decision. Final branch HEAD must be read from live PR #6 or worker handoff; it cannot be embedded in its own HEAD-producing status commit.
