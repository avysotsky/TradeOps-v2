# Institutional platform workstream registry — 2026-10-25

**MANDATORY OPERATING POLICY (owner confirmed 2026-10-25): GitHub Actions is FORBIDDEN for TradeOps.** GitHub serves only as Git source, Issues, reviews and Draft PR transport. All acceptance must run locally on the owner's WSL2 Ubuntu / Docker environment at a verified immutable HEAD SHA. The absence of hosted GitHub Checks/Actions must NEVER be treated as a reason to activate Actions or as a permanent acceptance blocker. Actual local tests may still be NOT_RUN until executed and evidenced.

**Owner / orchestrator chat:** orchestrator only; no feature/production code here. The GitHub issues are the authoritative executable worker assignments. **No worker is running solely because an issue or branch exists.** Each worker must be started by the user in a separate new ChatGPT chat.

Master roadmap: [TO-v2-56 Draft PR #56](https://github.com/avysotsky/TradeOps-v2/pull/56) and `INSTITUTIONAL_PLATFORM_MASTER_ROADMAP_2026-10-25.md`.
Repository: private `avysotsky/TradeOps-v2`.
Verified main source SHA at assignment: `c7a0d29028d7cf4958a26aa4879b99b3e39f7b14`.
Verified Deribit frontend source SHA (PR #55 HEAD) at assignment: `e4a067c9422845e9dd4579a36a728737f036185f`.
Existing stacked PRs: #54 clock diagnostics (base TO-v2-50), #55 browser private-read freshness (base #54), #56 master roadmap (base main). They are Draft, **not** merged. F1 stacks on #55 and MUST NOT be merged independently before its ancestor is accepted.

## Reserved worker workstreams

| ID | Issue | Reserved branch | Base | Exclusive file ownership | State |
|---|---|---|---|---|---|
| G0-01 | [#57](https://github.com/avysotsky/TradeOps-v2/issues/57), [PR #61](https://github.com/avysotsky/TradeOps-v2/pull/61) | `TradeOps/g0-01-baseline-readiness-audit` @ `10d00a98` | main @ `c7a0d2` | `docs/orchestration/workstreams/G0_01_*` | AUDIT DELIVERABLE ACCEPTED; platform NO-GO; Draft, no merge |
| B1-01 | [#58](https://github.com/avysotsky/TradeOps-v2/issues/58), [PR #62](https://github.com/avysotsky/TradeOps-v2/pull/62) | `TradeOps/b1-01-oms-domain-identity-contracts` @ `a799aec7` | main @ `c7a0d2` | additive domain/application contracts + named tests + OMS design docs | CONDITIONAL CODE ACCEPT; local 602/602 reported; partial-fill cancel-reject regression required; Draft/no merge |
| F1-01 | [#59](https://github.com/avysotsky/TradeOps-v2/issues/59), [PR #63](https://github.com/avysotsky/TradeOps-v2/pull/63), [VIS #68](https://github.com/avysotsky/TradeOps-v2/issues/68) | `TradeOps/f1-01-deribit-react-van-parity` @ `cd7f06c3` | #55 HEAD @ `e4a067c` | Deribit React UI + offline synthetic browser launcher, no backend | source/harness reviewed, CI-01 exact-HEAD and real Chromium screenshots NOT RUN; Draft/no merge |
| F2-01 | [#60](https://github.com/avysotsky/TradeOps-v2/issues/60), [PR #64](https://github.com/avysotsky/TradeOps-v2/pull/64), [VIS #69](https://github.com/avysotsky/TradeOps-v2/issues/69) | `TradeOps/f2-01-coincall-react-workstation` @ `a586c0d8` | main @ `c7a0d2` | Standalone CoinCall React + modal 2-leg mock books + offline browser QA | source/harness reviewed, 9 unit/20 React/14 VAN checks DECLARED, runtime NOT RUN; Draft/no merge |

| CI-01 | [#65](https://github.com/avysotsky/TradeOps-v2/issues/65), [PR #66](https://github.com/avysotsky/TradeOps-v2/pull/66) | `TradeOps/ci-01-exact-sha-dotnet10-react-gates` @ `1ba18fa0` | main @ `c7a0d2` | `scripts/ci/**`, `docs/ci/**` only; zero workflow changes | CODE REVIEW ACCEPT; owner local 651/651 reported, manifests not independently retrieved; Draft/no merge |

| B2-01 | [#67](https://github.com/avysotsky/TradeOps-v2/issues/67), [PR #70](https://github.com/avysotsky/TradeOps-v2/pull/70) | `TradeOps/b2-01-durable-command-journal` @ `2a96c52b` | B1 PR #62 HEAD | New OMS journal, Outbox, Inbox, migration, PG17 tests | SOURCE BLOCKERS FIXED; 19 PG + 5 unit tests written, runtime NOT RUN; awaiting CI-01 local mode #71; Draft/no merge |

## React worker handoff review — 2026-10-25

- **F1 #63** latest `cd7f06c31206b20d135d595c21791a7f9ff26c85`: has actual source-controlled WSL2/Docker offline fixture runner with Node24/Playwright and no-host-port Docker Chromium; useful and scope-preserving. No owner-machine Node24 suite/build, browser PNG or DOM report has been provided for this SHA. Visual/layout screenshot gate is **NOT RUN**, not ACCEPT; other VAN features still partial/missing. Existing CI-01 must be run separately on exact PR63 HEAD and F1 visual wrapper with owner Windows↔WSL2. The browser screenshot is offline fixture only, not real market L2 recovery evidence.
- **F2 #64** latest `a586c0d843385d6a5f89a5e9215f2756fff2cf4a`: typed mock spread dialog, 2 independent mock books, contract selector and offline VAN static screenshot runner added. No owner-machine Node24, 20 browser assertions, 14 VAN geometry assertions or images provided for this SHA; **NOT RUN**. Runner has a minor reporter-compatibility counting issue; do not infer test counts from `browser 20 checks` string without actual JSON/TAP. Original VAN JavaScript is intentionally disabled in static comparison.
- Reviews are posted on both PRs; SOURCE-ONLY REVIEW complete, FINAL ACCEPTANCE NOT GIVEN. GitHub Actions remains forbidden; zero merges.

## Orchestrator status recheck — 2026-10-25

- **B2-01 #70** HEAD `41ddac98c2601025e5c47475fdd57185746dd42b`, base B1 worker branch: six scoped new files. **BLOCKED (CHANGES REQUIRED):** SQL migration `20261025193000_AddOmsDurableJournal.cs` uses invalid PostgreSQL `AS $...# Institutional platform workstream registry — 2026-10-25

**MANDATORY OPERATING POLICY (owner confirmed 2026-10-25): GitHub Actions is FORBIDDEN for TradeOps.** GitHub serves only as Git source, Issues, reviews and Draft PR transport. All acceptance must run locally on the owner's WSL2 Ubuntu / Docker environment at a verified immutable HEAD SHA. The absence of hosted GitHub Checks/Actions must NEVER be treated as a reason to activate Actions or as a permanent acceptance blocker. Actual local tests may still be NOT_RUN until executed and evidenced.

**Owner / orchestrator chat:** orchestrator only; no feature/production code here. The GitHub issues are the authoritative executable worker assignments. **No worker is running solely because an issue or branch exists.** Each worker must be started by the user in a separate new ChatGPT chat.

Master roadmap: [TO-v2-56 Draft PR #56](https://github.com/avysotsky/TradeOps-v2/pull/56) and `INSTITUTIONAL_PLATFORM_MASTER_ROADMAP_2026-10-25.md`.
Repository: private `avysotsky/TradeOps-v2`.
Verified main source SHA at assignment: `c7a0d29028d7cf4958a26aa4879b99b3e39f7b14`.
Verified Deribit frontend source SHA (PR #55 HEAD) at assignment: `e4a067c9422845e9dd4579a36a728737f036185f`.
Existing stacked PRs: #54 clock diagnostics (base TO-v2-50), #55 browser private-read freshness (base #54), #56 master roadmap (base main). They are Draft, **not** merged. F1 stacks on #55 and MUST NOT be merged independently before its ancestor is accepted.

## Reserved worker workstreams

| ID | Issue | Reserved branch | Base | Exclusive file ownership | State |
|---|---|---|---|---|---|
| G0-01 | [#57](https://github.com/avysotsky/TradeOps-v2/issues/57), [PR #61](https://github.com/avysotsky/TradeOps-v2/pull/61) | `TradeOps/g0-01-baseline-readiness-audit` @ `10d00a98` | main @ `c7a0d2` | `docs/orchestration/workstreams/G0_01_*` | AUDIT DELIVERABLE ACCEPTED; platform NO-GO; Draft, no merge |
| B1-01 | [#58](https://github.com/avysotsky/TradeOps-v2/issues/58), [PR #62](https://github.com/avysotsky/TradeOps-v2/pull/62) | `TradeOps/b1-01-oms-domain-identity-contracts` @ `a799aec7` | main @ `c7a0d2` | additive domain/application contracts + named tests + OMS design docs | CONDITIONAL CODE ACCEPT; local 602/602 reported; partial-fill cancel-reject regression required; Draft/no merge |
| F1-01 | [#59](https://github.com/avysotsky/TradeOps-v2/issues/59), [PR #63](https://github.com/avysotsky/TradeOps-v2/pull/63), [VIS #68](https://github.com/avysotsky/TradeOps-v2/issues/68) | `TradeOps/f1-01-deribit-react-van-parity` @ `f3ecc615` | #55 HEAD @ `e4a067c` | `apps/trade-terminal/src/**` UI composition/CSS and UI-only tests/docs | new HEAD: browser runner prepared, Node24 exact-SHA + VAN screenshots NOT RUN; Draft/no merge |
| F2-01 | [#60](https://github.com/avysotsky/TradeOps-v2/issues/60), [PR #64](https://github.com/avysotsky/TradeOps-v2/pull/64), [VIS #69](https://github.com/avysotsky/TradeOps-v2/issues/69) | `TradeOps/f2-01-coincall-react-workstation` @ `eb985661` | main @ `c7a0d2` | NEW `apps/coincall-terminal/**` and CoinCall design docs | new HEAD: 9 tests/18 Chromium assertions declared, Node24 and real screenshots NOT RUN; Draft/no merge |

| CI-01 | [#65](https://github.com/avysotsky/TradeOps-v2/issues/65), [PR #66](https://github.com/avysotsky/TradeOps-v2/pull/66) | `TradeOps/ci-01-exact-sha-dotnet10-react-gates` @ `1ba18fa0` | main @ `c7a0d2` | `scripts/ci/**`, `docs/ci/**` only; zero workflow changes | CODE REVIEW ACCEPT; owner local 651/651 reported, manifests not independently retrieved; Draft/no merge |

| B2-01 | [#67](https://github.com/avysotsky/TradeOps-v2/issues/67), [PR #70](https://github.com/avysotsky/TradeOps-v2/pull/70) | `TradeOps/b2-01-durable-command-journal` @ `41ddac98` | B1 PR #62 HEAD | New OMS journal, Outbox, Inbox, migration, isolated PG17 tests | BLOCKED: PostgreSQL migration invalid AS $ delimiter; missing-PG tests false-pass; .NET/PG NOT RUN; Draft/no merge |

 / `DO $...# Institutional platform workstream registry — 2026-10-25

**MANDATORY OPERATING POLICY (owner confirmed 2026-10-25): GitHub Actions is FORBIDDEN for TradeOps.** GitHub serves only as Git source, Issues, reviews and Draft PR transport. All acceptance must run locally on the owner's WSL2 Ubuntu / Docker environment at a verified immutable HEAD SHA. The absence of hosted GitHub Checks/Actions must NEVER be treated as a reason to activate Actions or as a permanent acceptance blocker. Actual local tests may still be NOT_RUN until executed and evidenced.

**Owner / orchestrator chat:** orchestrator only; no feature/production code here. The GitHub issues are the authoritative executable worker assignments. **No worker is running solely because an issue or branch exists.** Each worker must be started by the user in a separate new ChatGPT chat.

Master roadmap: [TO-v2-56 Draft PR #56](https://github.com/avysotsky/TradeOps-v2/pull/56) and `INSTITUTIONAL_PLATFORM_MASTER_ROADMAP_2026-10-25.md`.
Repository: private `avysotsky/TradeOps-v2`.
Verified main source SHA at assignment: `c7a0d29028d7cf4958a26aa4879b99b3e39f7b14`.
Verified Deribit frontend source SHA (PR #55 HEAD) at assignment: `e4a067c9422845e9dd4579a36a728737f036185f`.
Existing stacked PRs: #54 clock diagnostics (base TO-v2-50), #55 browser private-read freshness (base #54), #56 master roadmap (base main). They are Draft, **not** merged. F1 stacks on #55 and MUST NOT be merged independently before its ancestor is accepted.

## Reserved worker workstreams

| ID | Issue | Reserved branch | Base | Exclusive file ownership | State |
|---|---|---|---|---|---|
| G0-01 | [#57](https://github.com/avysotsky/TradeOps-v2/issues/57), [PR #61](https://github.com/avysotsky/TradeOps-v2/pull/61) | `TradeOps/g0-01-baseline-readiness-audit` @ `10d00a98` | main @ `c7a0d2` | `docs/orchestration/workstreams/G0_01_*` | AUDIT DELIVERABLE ACCEPTED; platform NO-GO; Draft, no merge |
| B1-01 | [#58](https://github.com/avysotsky/TradeOps-v2/issues/58), [PR #62](https://github.com/avysotsky/TradeOps-v2/pull/62) | `TradeOps/b1-01-oms-domain-identity-contracts` @ `a799aec7` | main @ `c7a0d2` | additive domain/application contracts + named tests + OMS design docs | CONDITIONAL CODE ACCEPT; local 602/602 reported; partial-fill cancel-reject regression required; Draft/no merge |
| F1-01 | [#59](https://github.com/avysotsky/TradeOps-v2/issues/59), [PR #63](https://github.com/avysotsky/TradeOps-v2/pull/63), [VIS #68](https://github.com/avysotsky/TradeOps-v2/issues/68) | `TradeOps/f1-01-deribit-react-van-parity` @ `f3ecc615` | #55 HEAD @ `e4a067c` | `apps/trade-terminal/src/**` UI composition/CSS and UI-only tests/docs | new HEAD: browser runner prepared, Node24 exact-SHA + VAN screenshots NOT RUN; Draft/no merge |
| F2-01 | [#60](https://github.com/avysotsky/TradeOps-v2/issues/60), [PR #64](https://github.com/avysotsky/TradeOps-v2/pull/64), [VIS #69](https://github.com/avysotsky/TradeOps-v2/issues/69) | `TradeOps/f2-01-coincall-react-workstation` @ `eb985661` | main @ `c7a0d2` | NEW `apps/coincall-terminal/**` and CoinCall design docs | new HEAD: 9 tests/18 Chromium assertions declared, Node24 and real screenshots NOT RUN; Draft/no merge |

| CI-01 | [#65](https://github.com/avysotsky/TradeOps-v2/issues/65), [PR #66](https://github.com/avysotsky/TradeOps-v2/pull/66) | `TradeOps/ci-01-exact-sha-dotnet10-react-gates` @ `1ba18fa0` | main @ `c7a0d2` | `scripts/ci/**`, `docs/ci/**` only; zero workflow changes | CODE REVIEW ACCEPT; owner local 651/651 reported, manifests not independently retrieved; Draft/no merge |

| B2-01 | [#67](https://github.com/avysotsky/TradeOps-v2/issues/67), [PR #70](https://github.com/avysotsky/TradeOps-v2/pull/70) | `TradeOps/b2-01-durable-command-journal` @ `41ddac98` | B1 PR #62 HEAD | New OMS journal, Outbox, Inbox, migration, isolated PG17 tests | BLOCKED: PostgreSQL migration invalid AS $ delimiter; missing-PG tests false-pass; .NET/PG NOT RUN; Draft/no merge |

 instead of valid dollar-quote pairs; real PG17 migrations cannot execute as written. `OmsJournalPostgresTests.WithPostgres` silently returns when environment connection absent, yielding false green xUnit; make PostgreSQL acceptance explicit/fail-closed. None of B2 .NET10/PG17 test matrix has been executed. Code review comment posted on PR #70; no merge.
- **F1 #63** HEAD `f3ecc615dd5a24ff239833a1ec82a6deabbebccf`, existing stacked base #55: browser Playwright capture tool added plus UI/chart/tab code. Old 42/42 Node tests refer to earlier `e1b7c27`; **current** exact HEAD Node24 build & real 1440x900 / 390x844 VAN comparisons **NOT RUN**. Functional VAN parity incomplete (historical OHLC/funding/options/spreads).
- **F2 #64** HEAD `eb9856610150585ee42f69de54b7c3d6be8b9682`: 2D spreads matrix, mock account/instrument and offline original VAN reference comparison scripts prepared. Old 7/7 refer to previous `9261e94e`; **current** exact HEAD Node24 build/9 unit checks/18 Chromium smoke/1440x900+768x900 screenshot comparison **NOT RUN**. Original VAN JS disabled in safe reference, so these screenshots can only show HTML/CSS layout, not trading parity.
- Main `c7a0d29028d7cf4958a26aa4879b99b3e39f7b14` unchanged; all PRs remain Draft, GitHub Actions forbidden, MAINNET READ-ONLY, **zero merges authorized**.

## B2 latest review and local test dependency — 2026-10-25

- B2-01 [PR #70](https://github.com/avysotsky/TradeOps-v2/pull/70) updated to `2a96c52b1ec0e5ef425bd87a575f457b794864ab`. Orchestrator verified previously blocking PostgreSQL migration dollar-quotes and no-PG false PASS are fixed in source. 19 real-PG17 `[Fact]` and 5 pure contract cases are present; actual build, migration and runtime tests **NOT RUN**, so PR remains Draft **CONDITIONAL SOURCE ACCEPT / RUNTIME GATE PENDING**.
- CI-01 follow-up [Issue #71](https://github.com/avysotsky/TradeOps-v2/issues/71), on existing PR #66 branch, owns extending the centralized local WSL2 Ubuntu/Docker runner with B2 exact-SHA mode 70, disposable PG17, .NET10 and evidence report. No changes to GitHub Actions, to the active Windows branch, to ResidentChat services or to B2 source code. Owner triggers one PowerShell block; worker consumes console/log evidence. No merger of B2 before running and reviewing test results.

## CI-01 handoff review, 2026-10-25

[PR #66](https://github.com/avysotsky/TradeOps-v2/pull/66) HEAD `1ba18fa0b94e7251d2b40c9d01d2e3d4912991c9`: orchestrator inspected all 6 changed files and placed **CODE REVIEW ACCEPT** comment. ZERO `.github/workflows/**` changes; GitHub Actions remains forbidden. Runner isolates exact SHA WSL2/Docker execution, Mock-only PG17, Node24 and .NET10. Worker documents owner-local test evidence B1 #62 `a799aec7` 602/602, F1 #63 `e1b7c27` 42/42, F2 #64 `9261e94` 7/7, aggregate 651/651 PASS with builds and PG Mock smoke. GitHub exact PR heads still match the claimed tested SHAs, and the sole commit after tested CI runner HEAD was documentation-only. The actual `summary.json` and logs are retained only under the owner's WSL `~/tradeops-acceptance-results` and **not independently readable by the orchestrator in this chat**. Frontend screenshot parity is NOT RUN; older Deribit PR stack remains unmerged. Do not merge until explicit owner authorization and relevant gates.

## 2026-10-25 orchestration check

All four assigned workers submitted separate Draft PRs, each with correct base and no out-of-scope paths. `main` remains `c7a0d29028d7cf4958a26aa4879b99b3e39f7b14`. The GitHub status/check and PR-triggered workflow APIs returned **no exact-HEAD CI** for #61–#64. Orchestrator posted reviews on all four Draft PRs: G0 report accepted as a *documentation audit only*, keeping its institutional execution NO-GO; B1 contract code awaits actual .NET 10 compilation and tests; F1 awaits Node build/tests and exact browser comparison; F2 awaits Vite TypeScript build and actual browser comparison (self-reported pure test 4/4 is not CI). **0 merges authorized.** Next prerequisite: implement local Docker .NET10/Node24 exact-SHA acceptance and capture execution evidence without GitHub Actions.


### CI-01 correction / no GitHub Actions, 2026-10-25
CI-01 [Issue #65](https://github.com/avysotsky/TradeOps-v2/issues/65) and [Draft PR #66](https://github.com/avysotsky/TradeOps-v2/pull/66) were initially written incorrectly around hosted GitHub Actions; owner explicitly **prohibits GitHub Actions**. Orchestrator has overridden Issue #65 and requested rework of PR #66: **remove all workflow changes** and deliver primary local WSL2/Docker acceptance runner with source SHA pinning, isolation, independent .NET 10 + Node 24, optional disposable PG17 Mock smoke and durable local evidence. No worker has permission to enable Actions or require hosted checks. The existing workflow in main is legacy code, not an authorized execution facility. B1/F1/F2 remain Draft until real LOCAL test/build evidence and required browser visual parity are obtained. No merge authorized.

## Next approved worker queue (2026-10-25)

- B2-01 [Issue #67](https://github.com/avysotsky/TradeOps-v2/issues/67): independent new worker chat; branch initialized from B1 exact HEAD. Task only durable OMS intent/command/outbox persistence and PG17 failure tests. It may not enable order sending or assume DB fencing prevents stale physical gateways. No merge, no mainnet mutations.
- F1-VIS [Issue #68](https://github.com/avysotsky/TradeOps-v2/issues/68): follow-up to **existing F1 chat** using original F1 branch/PR #63, browser screenshots at 1440x900 and 390x844 against actual VAN source. No new conflicting branch.
- F2-VIS [Issue #69](https://github.com/avysotsky/TradeOps-v2/issues/69): follow-up to **existing F2 chat** using original F2 branch/PR #64, browser screenshots at 1440x900 and 768x900 against actual VAN source. No new conflicting branch.
- B1 PR #62 conditional review notes a missing test for CancelPending -> Accepted with retained partial fills and requires explicit full command payload in B2; coordinate if B1 HEAD changes. Previously reported 651/651 local suites still cannot replace browser screenshots. GitHub Actions is forbidden; WSL2/Docker exact SHA only.

## Dependency and noncollision protocol
- G0 is the *first acceptance gate* for B1 merge, frontend integration and subsequent B2/B3/FIX. B1 may prepare purely additive contracts while G0 runs; no risk-affecting behavior until G0 accepted.
- F1 owns only current Deribit UI, based on PR #55. #54/#55 remain frozen upstream unless independently approved.
- F2 is fully self-contained in `apps/coincall-terminal/**`; do not write to `apps/trade-terminal/**` or shared root packages. Common packages are requested via this orchestrator after UI inventory.
- B1 is independent of F1/F2 and may not change runtime exchange adapters, PostgreSQL migrations or live order entry.
- Workers cannot merge their own PR or edit a sibling's branch/files. One Draft PR per assignment, correct base ref. Separate branches for all extensions or blocker fixes.
- Market data / RAW L2 code and Deribit time diagnostics are outside all four new assignment scopes.
- MAINNET STRICT READ-ONLY always; no live trading, no credential collection, no secret exfiltration. Real TESTNET order mutations likewise require explicit owner authorization in their future implementation PR.

## Orchestrator review checklist
1. Verify referenced Issue scope, exact branch/base SHA and diff paths; detect collisions and inherited PR stack.
2. Read all changed code and tests; enforce strong typing, account+venue scoped identifiers, no unsafe shortcuts or external secrets.
3. Verify exact-HEAD **LOCAL WSL2/Docker** build/test logs and artifact summaries, test counts/exit codes, negative/failure case coverage. GitHub Actions is prohibited and irrelevant. Missing real local evidence means NOT_RUN / NOT VERIFIED, not a reason to re-enable Actions.
4. Inspect trading safety invariants, mainnet flags, no write endpoints added/activated, no missing audit, no bad failover behavior.
5. Require deterministic acceptance evidence: fixture outputs/screenshots for UI and failure-injection trace for OMS, no unverified production claims.
6. Return `ACCEPT`, `CHANGES REQUESTED` with concrete file-specific comments, or `BLOCKED`; only then ask owner for merge authorization. Do not silently merge.
7. Update this registry status and next workstream priorities when a worker publishes a Draft PR.

## User-start chat prompts
Each worker chat should begin with: “Ты воркер <ID> TradeOps-v2. Выполни GitHub Issue #<n> строго в своей ветке. Сначала прочитай Issue и master roadmap PR #56. Делай только свой scope, открой Draft PR с правильным base, отчитайся по SHA/тестам/рискам. Оркестратор проверит PR и решит о приёмке.”
