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
| G0-01 | [#57](https://github.com/avysotsky/TradeOps-v2/issues/57) | `TradeOps/g0-01-baseline-readiness-audit` | main @ `c7a0d2` | `docs/orchestration/workstreams/G0_01_*` | ASSIGNED; NOT STARTED |
| B1-01 | [#58](https://github.com/avysotsky/TradeOps-v2/issues/58) | `TradeOps/b1-01-oms-domain-identity-contracts` | main @ `c7a0d2` | additive domain/application contracts + named tests + OMS design docs | ASSIGNED; NOT STARTED |
| F1-01 | [#59](https://github.com/avysotsky/TradeOps-v2/issues/59) | `TradeOps/f1-01-deribit-react-van-parity` | #55 HEAD @ `e4a067c` | `apps/trade-terminal/src/**` UI composition/CSS and UI-only tests/docs | ASSIGNED; NOT STARTED |
| F2-01 | [#60](https://github.com/avysotsky/TradeOps-v2/issues/60) | `TradeOps/f2-01-coincall-react-workstation` | main @ `c7a0d2` | NEW `apps/coincall-terminal/**` and CoinCall design docs | ASSIGNED; NOT STARTED |

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
