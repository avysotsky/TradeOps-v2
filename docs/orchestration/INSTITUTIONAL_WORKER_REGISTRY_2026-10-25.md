# Institutional platform workstream registry — 2026-10-25

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
| B1-01 | [#58](https://github.com/avysotsky/TradeOps-v2/issues/58), [PR #62](https://github.com/avysotsky/TradeOps-v2/pull/62) | `TradeOps/b1-01-oms-domain-identity-contracts` @ `814f7ea5` | main @ `c7a0d2` | additive domain/application contracts + named tests + OMS design docs | REVIEWED; build and test evidence REQUIRED; Draft, no merge |
| F1-01 | [#59](https://github.com/avysotsky/TradeOps-v2/issues/59), [PR #63](https://github.com/avysotsky/TradeOps-v2/pull/63) | `TradeOps/f1-01-deribit-react-van-parity` @ `246bd259` | #55 HEAD @ `e4a067c` | `apps/trade-terminal/src/**` UI composition/CSS and UI-only tests/docs | REVIEWED; React build and screenshots REQUIRED; Draft, no merge |
| F2-01 | [#60](https://github.com/avysotsky/TradeOps-v2/issues/60), [PR #64](https://github.com/avysotsky/TradeOps-v2/pull/64) | `TradeOps/f2-01-coincall-react-workstation` @ `4322c8aa` | main @ `c7a0d2` | NEW `apps/coincall-terminal/**` and CoinCall design docs | REVIEWED; 4/4 local Node tests self-reported, Vite build and screenshot evidence REQUIRED; Draft, no merge |

| CI-01 | [#65](https://github.com/avysotsky/TradeOps-v2/issues/65) | `TradeOps/ci-01-exact-sha-dotnet10-react-gates` @ `c7a0d2` (reserved) | main @ `c7a0d2` | `.github/workflows/**`, NEW `scripts/ci/**` and `docs/ci/**` only | ASSIGNED; NOT STARTED; .NET 10 / React exact-SHA gate |

## 2026-10-25 orchestration check

All four assigned workers submitted separate Draft PRs, each with correct base and no out-of-scope paths. `main` remains `c7a0d29028d7cf4958a26aa4879b99b3e39f7b14`. The GitHub status/check and PR-triggered workflow APIs returned **no exact-HEAD CI** for #61–#64. Orchestrator posted reviews on all four Draft PRs: G0 report accepted as a *documentation audit only*, keeping its institutional execution NO-GO; B1 contract code awaits actual .NET 10 compilation and tests; F1 awaits Node build/tests and exact browser comparison; F2 awaits Vite TypeScript build and actual browser comparison (self-reported pure test 4/4 is not CI). **0 merges authorized.** Next prerequisite: repair .NET 8 vs net10 CI toolchain and run pinned exact-HEAD acceptance.


### New CI-01 lane, 2026-10-25
CI-01 [Issue #65](https://github.com/avysotsky/TradeOps-v2/issues/65) reserves the dedicated branch from main for repairing .NET 8 SDK vs net10 targets and producing independent exact-HEAD React/.NET acceptance gates. Workflow code is NOT implemented yet; a separate worker chat must be started explicitly. Last observable GitHub Actions run is dated 2026-10-09; repository Actions availability/permissions remains UNVERIFIED. CI-01 owns no `src/`, no `apps/` and no trading API changes. B1/F1/F2 stay draft until their exact SHA receives actual build/test evidence. F1’s large unmerged parent stack requires explicit inherited base verification.

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
3. Verify test and build commands, actual CI conclusion with commit SHA, negative/failure case coverage. Lack of CI means NOT VERIFIED.
4. Inspect trading safety invariants, mainnet flags, no write endpoints added/activated, no missing audit, no bad failover behavior.
5. Require deterministic acceptance evidence: fixture outputs/screenshots for UI and failure-injection trace for OMS, no unverified production claims.
6. Return `ACCEPT`, `CHANGES REQUESTED` with concrete file-specific comments, or `BLOCKED`; only then ask owner for merge authorization. Do not silently merge.
7. Update this registry status and next workstream priorities when a worker publishes a Draft PR.

## User-start chat prompts
Each worker chat should begin with: “Ты воркер <ID> TradeOps-v2. Выполни GitHub Issue #<n> строго в своей ветке. Сначала прочитай Issue и master roadmap PR #56. Делай только свой scope, открой Draft PR с правильным base, отчитайся по SHA/тестам/рискам. Оркестратор проверит PR и решит о приёмке.”
