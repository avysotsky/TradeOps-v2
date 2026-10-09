# Development Orchestrator v2 — Worker Registry (PRIVATE)

Status: **TO-V2-01A APPROVED / DF-V2-02B + DF-V2-02C INTEGRATED / DF-V2-02D REGISTERED**
Recorded on 2026-10-09. GitHub is source of truth; stale SHAs require refresh before execution/merge.

## Role boundary — mandatory
The Development Orchestrator plans the architecture, creates and assigns isolated branches, defines acceptance criteria, inspects CI, reviews PR diffs and merges after gates pass. **It does not implement feature/production/test code.** Implementations and test changes belong exclusively to assigned worker chats. Documentation of orchestration and pre-created task branches are permitted here.

A new chat is a worker only after the user starts it with its assignment. Registering branches or opening draft PRs does **not** mean an autonomous worker is running.

## Repository invariants
- `avysotsky/TradeOps-v2`: PRIVATE, TO-V2-01A docs-only design approved and merged in PR #2 at `56240235b7b7aa3bc40a30c68183d9490a2abeec`. Exact-head PR build `37920096539` SUCCESS (576/576, zero build warnings). Post-merge run `37921175907` triggered; check live conclusion before claiming it passed.
- `avysotsky/DocFlow-v2`: PRIVATE, main `b7a6931bc9bc3d31ab28a8cc8dc7369f96bb92ce`. DF-V2-02B PR #4 merged (36/36 differential tests in main run `37920526539` SUCCESS); DF-V2-02C PR #5 merged (audit finding 3 BLOCKER / 10 MUST_FIX / 4 FOLLOW_UP on earlier 02A baseline). Post-merge Python `37920526536` and .NET `37920526551` both SUCCESS. Next DF-V2-02D is registered in draft PR #6, not yet accepted.
- `avysotsky/TradeOps` and `avysotsky/DocFlow`: PUBLIC + ARCHIVED; previous URL and deep-link checks passed. Never push v2 content to v1 or make v2 public.
- DocFlow-v2 PR #2: 58/58 normalization parity fixture checks SUCCESS; PR #3: restricted schema subset foundation merged. **Neither proves full Python Draft 2020-12 extraction parity.**
- `DocFlow.sln` CI currently does not build the separately introduced `DocFlow.SchemaExtraction` project; a dedicated PR-only schema-gate workflow is the prior proof. This CI gap is an explicit review/repair item.

## Registered parallel tracks

| ID | Repo | Worker branch | Draft PR | Assignment | Ownership | State |
|---|---|---|---|---|---|---|
| DF-V2-02B (MERGED) | DocFlow-v2 | `DocFlow/df-v2-02b-native-python-contract-diff` | [#4](https://github.com/avysotsky/DocFlow-v2/pull/4) | [DF_V2_02B_ASSIGNMENT.md](https://github.com/avysotsky/DocFlow-v2/blob/DocFlow/df-v2-02b-native-python-contract-diff/docs/workstreams/DF_V2_02B_ASSIGNMENT.md) | Native/Python differential test harness and limited application-core fixes | MERGED / validated subset |
| DF-V2-02C (MERGED) | DocFlow-v2 | `DocFlow/df-v2-02c-contract-security-audit` | [#5](https://github.com/avysotsky/DocFlow-v2/pull/5) | [DF_V2_02C_AUDIT_ASSIGNMENT.md](https://github.com/avysotsky/DocFlow-v2/blob/DocFlow/df-v2-02c-contract-security-audit/docs/workstreams/DF_V2_02C_AUDIT_ASSIGNMENT.md) | Audit docs only, not code | MERGED / review follow-up required |
| TO-V2-01A (MERGED) | TradeOps-v2 | `TradeOps/to-v2-01a-dual-topology-design` | [#2](https://github.com/avysotsky/TradeOps-v2/pull/2) | [TO_V2_01A_ASSIGNMENT.md](https://github.com/avysotsky/TradeOps-v2/blob/TradeOps/to-v2-01a-dual-topology-design/docs/orchestration/workstreams/TO_V2_01A_ASSIGNMENT.md) | Topology/packaging architecture docs only | APPROVED / MERGED |

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

## 2026-10-09 integration update

- **TO-V2-01A approved and merged**: design PR #2 at `56240235b7b7aa3bc40a30c68183d9490a2abeec`. The accepted ADR is `docs/orchestration/workstreams/TO_V2_01A_TOPOLOGY_DESIGN.md`: distributed authenticated DocFlow HTTP and local in-process DI use the *same* future native DocFlow extraction facade. Production shared modules are proposed via immutable private NuGet; local project references are for prototype. No runtime changes implemented or released in TO-V2-01A.
- **DocFlow DF-V2-02B/C integrated** at main `b7a6931bc9bc3d31ab28a8cc8dc7369f96bb92ce`. The native differential suite verifies 36 synthetic subset cases only; full JSON Schema 2020-12 is not implemented. Audit findings need explicit remediation before production API.
- **DF-V2-02D next worker**: [draft PR #6](https://github.com/avysotsky/DocFlow-v2/pull/6) and `docs/workstreams/DF_V2_02D_ASSIGNMENT.md`, focused on normalized-document ingress and the remaining appointed blockers. Integration is not authorized before its CI and audit gates.
- **Subsequent dependencies**: DF-V2-02E DocFlow facade/provider composition → DF-V2-03 authenticated DocFlow ASP.NET HTTP host → DF-V2-04 private package publication and verified consumer permission → TO-V2-01B direct TradeOps port adapter → monolith host/dual-topology parity; worker branches to be created when upstream contracts are ready, not prematurely.
- **Safety**: v1 public repositories remain Public+Archived; v2 remains private; no live trading or broker mutation is approved.

## Parallel planning after TO-V2-01A approval (2026-10-09)

**Live checks:** TradeOps-v2 `main=dea68be61c05e3ae4850fa5eb12c456d7cb3a2b2` with post-merge CI runs `37921175907` and `37921256586` both SUCCESS. DocFlow-v2 `main=b7a6931bc9bc3d31ab28a8cc8dc7369f96bb92ce`, native/Python differential `37920526539` SUCCESS on main.

- **DF-V2-02D — implementation, READY, no activity at verification:** private DocFlow-v2 draft [#6](https://github.com/avysotsky/DocFlow-v2/pull/6); registered HEAD `c5ac43ac31fa9e98b3e3438fc319d7751674c39e`. Owns normalized ingress/remediation and related native CI. Must complete/merge before implementing facade.
- **DF-V2-02E0 — newly registered independent design-only RFC:** private DocFlow-v2 branch `DocFlow/df-v2-02e0-facade-contract-design`, draft [#7](https://github.com/avysotsky/DocFlow-v2/pull/7); registered HEAD `5fb179f0f8d1ffd4de8c2625ac6fb1ef885d0615`. Scope: **only** `docs/workstreams/DF_V2_02E0_ASSIGNMENT.md` and `docs/workstreams/DF_V2_02E0_FACADE_CONTRACT_DESIGN.md`. Designs shared DocFlow facade, normalized+structured envelope, API/in-process mapping, ownership and acceptance tests, conditional on D. Does not implement code, change D files or authorize production.
- The two jobs may **run in parallel** with nonoverlapping file ownership. A worker runs only when a separate worker chat is started; registered draft PRs are not autonomous work.
- Proposed integration sequence: D code+CI+audit clearance → review E0 RFC against actual D public contract → later DF-V2-02E implementation worker (new branch/owner) → DF-V2-03 native authenticated HTTP → pinned private package/DI integration and TO-V2-01B. Do not create implementer E while D unresolved.
