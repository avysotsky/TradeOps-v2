# Development Orchestrator v2 — Worker Registry (PRIVATE)

Status: **TO-V2-01A, DF-V2-02B/C/D/E0/E MERGED; DF-V2-03 REGISTERED / NOT STARTED**
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

## Workstream integration acceptance — 2026-10-09, DF-V2-02D/E0

- **DF-V2-02D IMPLEMENTED / MERGED** into private DocFlow-v2 by [PR #6](https://github.com/avysotsky/DocFlow-v2/pull/6): merge commit `da1081586c5176d3c8ace65175b7329c5df7d4ae`. Canonical `ExtractNormalizedAsync` validates already-normalized ID/segment/fingerprint integrity without calling raw normalization, preserves `ExtractRawAsync` and legacy `ExtractAsync`, adds strict schema request metadata, optional request-aware backends, identity/name/cancel checks. Exact main-post-merge native differential `37929129214` **SUCCESS 58/58 + 46 native ingress assertions**, Python CI `37929129026` **SUCCESS**, .NET CI `37929129060` **SUCCESS**.
- **DF-V2-02E0 DESIGN RFC MERGED** in private DocFlow-v2 [PR #7](https://github.com/avysotsky/DocFlow-v2/pull/7): `39d01c71ad53059e00b64d66508038371b9435e2`. Adopted architectural **direction**: one DocFlow-owned extraction facade, explicit raw and already-normalized application calls, both normalized+structured artifacts, shared by authenticated DocFlow HTTP and direct TradeOps in-process adapter. Contract RFC includes proposed quotas/security and future phases, **not a claim of their implementation**; several details are still subject to worker acceptance/testing.
- **DF-V2-02E IMPLEMENTATION NOW REGISTERED**, private [DocFlow-v2 Draft PR #8](https://github.com/avysotsky/DocFlow-v2/pull/8), branch `DocFlow/df-v2-02e-extraction-facade-implementation`, assignment `docs/workstreams/DF_V2_02E_ASSIGNMENT.md`, registered HEAD `563761f14bda62ee2ac15a7696c1b29fcb6e1d1d` (assignment-only). Worker owns new facade library/test/CI paths; existing D core, API, Python, TradeOps and public v1 archives are out of scope. Registration does not start the worker autonomously.
- **Still not production-ready:** full JSON Schema Draft 2020-12/meta-schema parity, duplicate schema/backend JSON keys across language boundaries, arbitrary-precision numeric semantics, request/response size/depth/concurrency/time budgets, provider/HTTP auth/redaction, license review and .NET TFM support horizon remain gated. Python reference remains available for known unsupported capabilities; no silent fallback or live broker execution.
- **Next sequence:** DF-V2-02E offline facade CI -> separate DF-V2-03 secure ASP.NET extraction API -> DF-V2-04 pinned private NuGet + consumer ACL -> TO-V2-01B direct infrastructure port -> monolith composition/equivalence. Do not start adapter implementation before facade contract/test acceptance.


## Successor orchestrator reconciliation — 2026-10-09 15:34 Kyiv

Live GitHub source-of-truth snapshot; **do not treat mutable worker HEAD below as an approved release**:

- Private **TradeOps-v2** `main=11d577c2182a6c04f37f31bc6e5b36ac8a8d773e`; exact push build [37930352494](https://github.com/avysotsky/TradeOps-v2/actions/runs/37930352494) **SUCCESS**; zero open PRs at check. TO-V2-01A merged previously; no new TradeOps worker registered.
- Private **DocFlow-v2** `main=39d01c71ad53059e00b64d66508038371b9435e2`. Last code-bearing main `da1081586c5176d3c8ace65175b7329c5df7d4ae` passed native differential, Python and .NET workflows; subsequent E0 main commit is docs-only.
- One open DocFlow PR, [DF-V2-02E #8](https://github.com/avysotsky/DocFlow-v2/pull/8), **DRAFT**. Existing branch `DocFlow/df-v2-02e-extraction-facade-implementation`, observed 15:34 Kyiv `HEAD=14c8293e1f1bff08a35ad1af08bb02831df1548f`, moving during active worker activity. Do **not** recreate branch or start duplicate worker. An earlier exact SHA `6da4e3bebe9dd2ecce68364e7f921142809067b4` had [facade workflow 37930706948](https://github.com/avysotsky/DocFlow-v2/actions/runs/37930706948) **SUCCESS**, 12 scenarios / 202 assertions and 46 native ingress checks, 0 compiler warnings/errors, plus [.NET CI 37930707010](https://github.com/avysotsky/DocFlow-v2/actions/runs/37930707010) **SUCCESS**. These checks **do not** prove the newer `14c8293e` head.
- Initial review found changes confined to new facade assembly, isolated tests and dedicated workflow plus existing assignment; no existing normalized/schema core, DocFlow.Api or TradeOps edits. Worker status `docs/workstreams/DF_V2_02E_STATUS.md` not yet present at 15:34; PR title/body still registered as assignment-only. **NO MERGE until** worker explicitly finishes, status/PR handoff are updated, fresh file-scope/privacy/security and contract review pass, current exact SHA CI success (including facade build actually exercised), and post-merge main CI is checked.
- Private v2 repos remain PRIVATE. Public legacy TradeOps and DocFlow remain PUBLIC + ARCHIVED; no changes allowed.
- **Next:** integrate DF-V2-02E on acceptance; only then schedule DF-V2-03 (service-authenticated native ASP.NET HTTP extraction host) with a separate worker branch, full assignment and Draft PR. Follow approved DF-V2-04 → TO-V2-01B → TO-V2-01C → TO-V2-01D sequence. Full Draft 2020-12, service authorization and resource/security budgets remain unproven.

This is an orchestration state note, not worker implementation or release approval.


## 2026-10-09 integration acceptance — DF-V2-02E and registration — DF-V2-03

**Completed upstream:**

- Private DocFlow-v2 [DF-V2-02E PR #8](https://github.com/avysotsky/DocFlow-v2/pull/8) **APPROVED / SQUASH-MERGED** from \`DocFlow/df-v2-02e-extraction-facade-implementation\` exact worker HEAD \`30274e01aba0b3ae078db835ef92e6f7ad875750\`. Integrated main SHA \`6b2c8aab9d6170ba637672bc958e382ab678aacb\`.
- Independent premerge gate: \`37931156298\` Facade **SUCCESS** 12 scenarios / 212 assertions, 46 native normalized-ingress, schema subset; \`37931156306\` .NET **SUCCESS**. Scope exactly 11 E-owned new application/test/CI/handoff files, no upstream normalization/schema changes, no Python/API/TradeOps production changes or forbidden v1 edits. Worker handoff recorded in \`docs/workstreams/DF_V2_02E_STATUS.md\`, PR body + final exact-HEAD comment.
- **Post-merge exact DocFlow-v2 main SHA \`6b2c8aab9d6170ba637672bc958e382ab678aacb\`: [Facade run 37931582856](https://github.com/avysotsky/DocFlow-v2/actions/runs/37931582856) **SUCCESS**, [.NET CI run 37931582986](https://github.com/avysotsky/DocFlow-v2/actions/runs/37931582986) **SUCCESS**. This qualifies G1 **for the explicitly supported native schema subset only**, not public or full Draft 2020-12 service readiness.

**Next worker independently registered:**

- **DF-V2-03 — Secure ASP.NET Core Native HTTP Extraction Service**. Private repo \`avysotsky/DocFlow-v2\`, existing assigned branch \`DocFlow/df-v2-03-secure-http-extraction\`. Baseline SHA \`6b2c8aab9d6170ba637672bc958e382ab678aacb\`, registered assignment-only HEAD \`0ca5fdf3497945e2cceaaf8403f0ef9e2782edf2\`.
- [Draft PR #9](https://github.com/avysotsky/DocFlow-v2/pull/9); full assignment \`docs/workstreams/DF_V2_03_ASSIGNMENT.md\`. **REGISTERED, NOT STARTED** until user launches separate worker chat. Do not create another branch or PR. Orchestrator must verify fresh HEAD, compare, file ownership, exact API-auth CI and reviews prior to any merge.
- Ownership: \`src/DocFlow.Api/**\` secure extraction transport only, **new** \`tests/DocFlow.Api.Extraction.Tests/**\`, **new** \`.github/workflows/df-v2-03-http-extraction.yml\`, its own status+assignment files. Do not change \`DocumentsController\` behavior, normalized/schema/facade source, Python, TradeOps or public archived v1. No real LLM SDK or public deployment. Route should map existing Python v1 envelope to **the same** native facade, require real service authentication + \`docflow.extract\`, scoped resource limits and fail-closed boundaries. Native subset limitations and current unprotected persistence routes mean no whole-host public security approval.
- **Next after accepted DF-V2-03**: DF-V2-04 private immutable .NET packages, verified ACL/restore → TO-V2-01B direct in-process port → TO-V2-01C monolith host → TO-V2-01D deterministic topology equivalence; do not leapfrog dependency gates.

**Other live state at registration:** TradeOps-v2 PRIVATE \`main=fc5d5d0dbe475d551cbf996b875512c7c56183ba\`, push build \`37931093600\` SUCCESS; DocFlow-v2 PRIVATE, originals TradeOps/DocFlow PUBLIC+ARCHIVED unchanged.
