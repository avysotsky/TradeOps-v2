# Development Orchestrator v2 — Worker Registry (PRIVATE)

Status: **WORKSTREAMS REGISTERED — WORKER EXECUTION NOT YET STARTED**
Recorded on 2026-10-09. GitHub is source of truth; stale SHAs require refresh before execution/merge.

## Role boundary — mandatory
The Development Orchestrator plans the architecture, creates and assigns isolated branches, defines acceptance criteria, inspects CI, reviews PR diffs and merges after gates pass. **It does not implement feature/production/test code.** Implementations and test changes belong exclusively to assigned worker chats. Documentation of orchestration and pre-created task branches are permitted here.

A new chat is a worker only after the user starts it with its assignment. Registering branches or opening draft PRs does **not** mean an autonomous worker is running.

## Repository invariants
- `avysotsky/TradeOps-v2`: PRIVATE, main `ec3dc4aa51440aa38856bd8220492f57dd7df1ab`, full build run `37915153268` SUCCESS (576/576).
- `avysotsky/DocFlow-v2`: PRIVATE, main `eb7280306785fe52c1228fe3829e415edd01fa8e`, .NET main CI `37917316950` SUCCESS; native subset PR CI `37917127649` SUCCESS.
- `avysotsky/TradeOps` and `avysotsky/DocFlow`: PUBLIC + ARCHIVED; previous URL and deep-link checks passed. Never push v2 content to v1 or make v2 public.
- DocFlow-v2 PR #2: 58/58 normalization parity fixture checks SUCCESS; PR #3: restricted schema subset foundation merged. **Neither proves full Python Draft 2020-12 extraction parity.**
- `DocFlow.sln` CI currently does not build the separately introduced `DocFlow.SchemaExtraction` project; a dedicated PR-only schema-gate workflow is the prior proof. This CI gap is an explicit review/repair item.

## Registered parallel tracks

| ID | Repo | Worker branch | Draft PR | Assignment | Ownership | State |
|---|---|---|---|---|---|---|
| DF-V2-02B | DocFlow-v2 | `DocFlow/df-v2-02b-native-python-contract-diff` | [#4](https://github.com/avysotsky/DocFlow-v2/pull/4) | [DF_V2_02B_ASSIGNMENT.md](https://github.com/avysotsky/DocFlow-v2/blob/DocFlow/df-v2-02b-native-python-contract-diff/docs/workstreams/DF_V2_02B_ASSIGNMENT.md) | Native/Python differential test harness and limited application-core fixes | READY / not started |
| DF-V2-02C | DocFlow-v2 | `DocFlow/df-v2-02c-contract-security-audit` | [#5](https://github.com/avysotsky/DocFlow-v2/pull/5) | [DF_V2_02C_AUDIT_ASSIGNMENT.md](https://github.com/avysotsky/DocFlow-v2/blob/DocFlow/df-v2-02c-contract-security-audit/docs/workstreams/DF_V2_02C_AUDIT_ASSIGNMENT.md) | Audit docs only, not code | READY / not started |
| TO-V2-01A | TradeOps-v2 | `TradeOps/to-v2-01a-dual-topology-design` | [#2](https://github.com/avysotsky/TradeOps-v2/pull/2) | [TO_V2_01A_ASSIGNMENT.md](https://github.com/avysotsky/TradeOps-v2/blob/TradeOps/to-v2-01a-dual-topology-design/docs/orchestration/workstreams/TO_V2_01A_ASSIGNMENT.md) | Topology/packaging architecture docs only | READY / not started |

Exact registered draft PR branch HEADs at registration:
- DF-V2-02B: `81ed9afe3a2376ad160970165800a35ca2b35a2b`, original unfinished worker code before task note `3620c128bf2986b63075777a0e57eac06a74c546`. This CLI references **missing FixtureBackend**. Two unmerged code files must be recovered, not presumed complete.
- DF-V2-02C: `be1dbf32d2e286d46f67ce975aa4909d4ecfe578`.
- TO-V2-01A: `9bf8a2dc7816d6b62e26a2ff7dac4f87bb8f132b`.

## Dependencies / integration queue
1. **Independent audit DF-V2-02C** may start in parallel with **recovery DF-V2-02B** because ownership does not overlap. Audit issues are sent to orchestrator, then routed to B or a new remediation worker.
2. DF-V2-02B tests must prove explicit native-vs-Python compatibility for the supported schema subset, reject unsupported constructs and add CI that builds native code on private `main`. Unresolved Pydantic/2020-12 mismatch is a BLOCKER for any production exposure.
3. **TO-V2-01A** docs-only architecture design may run concurrently. It must not imply native API implementation is ready.
4. Only after a valid audit + differential contract gate may the orchestrator schedule **DF-V2-02D** full schema conformance and provider-boundary work, followed by **DF-V2-03** authenticated ASP.NET Core HTTP host and **TO-V2-01B** in-process assembly adapter/equivalence.
5. Preserve Python and legacy HTTP/CLI regression routes until equivalent native paths have passed real tests. Never authorize live broker mutation from this workstream.

## Mandatory handoff and merge gate
Each worker returns exact branch HEAD, compare against current main, per-file diff, tests, CI run IDs/status, privacy scan, contract changes, blockers and draft PR. Orchestrator verifies fresh main/branch and all expected checks on exact branch SHA, receives independent audit evidence, checks non-overlap and performs integration. A green run on **older** HEAD or on a build that skips the affected project does not satisfy this gate. No independent worker merges cross-workstream changes.

## Next orchestrator decision
Open separate worker chats using the assignment linked for #4, #5 and TradeOps-v2 #2. They do not start automatically. Give highest priority to DF-V2-02B and DF-V2-02C; TO-V2-01A can proceed concurrently or after those if resources are limited.
