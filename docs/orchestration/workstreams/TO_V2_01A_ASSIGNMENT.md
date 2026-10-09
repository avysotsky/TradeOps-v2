# TO-V2-01A — Distributed HTTP and Native In-Process Topology Design

Status: **REGISTERED / NOT STARTED**. Separate design-only worker; do not implement runtime code in this workstream.

## GitHub scope
- Repository: `avysotsky/TradeOps-v2` (Private).
- Assigned branch: `TradeOps/to-v2-01a-dual-topology-design`.
- Branch origin: main `ec3dc4aa51440aa38856bd8220492f57dd7df1ab`.
- Source TradeOps-v2 CI `37915153268`: SUCCESS, 576 tests passed.
- DocFlow-v2 code baseline `eb7280306785fe52c1228fe3829e415edd01fa8e` at assignment, PR #3 merged; more work on extraction remains.
- Both historical v1 repositories remain Public + Archived and MUST NOT be modified.

## Goal
Develop an implementable, independently reviewable architecture decision to support:
1. separately deployed `DocFlow.Api` and `TradeOps.Api` HTTP services; and
2. a single modular .NET process that composes **the very same application logic** in-process through dependency injection, with zero localhost HTTP calls internally.

Read `docs/orchestration/DUAL_TOPOLOGY_ARCHITECTURE_PLAN.md`, `docs/orchestration/V2_ACTIVE_ROADMAP.md`, and present code at:
- TradeOps `IDocFlowExtractionPort`, `DocFlowHttpExtractionAdapter`, registered HTTP client and real host API routes;
- DocFlow-v2 `DocFlow.Normalization`, `DocFlow.SchemaExtraction`, existing `DocFlow.Api` and FastAPI `/api/v1/extractions`.

Provide design choices with explicit tradeoffs:
- source ownership, assemblies vs NuGet vs source reuse, consuming a **private** DocFlow library securely, GitHub Actions/private package auth;
- preserving provider-neutral TradeOps port without pulling DocFlow types across the TradeOps domain;
- service-to-service identity and TLS, request/response limits, cancellation, failures and redacted logs;
- DI lifetimes, configuration/credentials, versioned schema contracts and test seams;
- exact practical migration order: native schema parity gate → DocFlow .NET API → HTTP contract tests → in-process adapter → equivalence tests, without losing existing Python/CLI regression routes;
- concrete acceptance tests showing HTTP vs in-process functional equivalence and ensuring no broker mutation/live trading;
- composition-host ownership, no redundant third repo until required.

## Ownership / exclusions
Allowed files: `docs/orchestration/workstreams/TO_V2_01A_TOPOLOGY_DESIGN.md` and this assignment/status file only.
Do not change any application or test code, CI, csproj, solution files, other branch, private visibility, or public v1.
Do not assert that .NET schema extraction or native DocFlow.Api is production-ready: currently only a restricted schema subset exists.

## Acceptance and handoff
Before work, re-check live GitHub main/branch SHAs, merged PRs and CI.
Present at least two viable module-distribution options with a **recommended** path, dependency/contract diagram, package version policy, security gates, rollback plan, and implementable next worker slices with nonoverlapping file ownership. Include a decision record (accepted/proposed/blocked), risks and unresolved upstream DF-V2-02B/02C issues. No fabricated validation claims.

Submit draft PR to TradeOps-v2 main. Do not merge. Return exact HEAD, PR URL and a concise architecture recommendation for orchestrator approval.
