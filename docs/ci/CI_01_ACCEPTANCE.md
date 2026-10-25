# CI-01 — Exact-SHA .NET 10 / React acceptance

**Work package:** [Issue #65](https://github.com/avysotsky/TradeOps-v2/issues/65)  
**Related:** [G0 audit #61](https://github.com/avysotsky/TradeOps-v2/pull/61), [master roadmap #56](https://github.com/avysotsky/TradeOps-v2/pull/56).  
**Change ownership:** only `.github/workflows/**`, `scripts/ci/**`, `docs/ci/**`. No OMS, terminal UI, trading API, migrations or test source has been edited.

## 1. Exact source refs inspected on 2026-10-25

| Target | SHA | Qualification needed |
| --- | --- | --- |
| Merged `main` | `c7a0d29028d7cf4958a26aa4879b99b3e39f7b14` | Baseline; React apps absent |
| B1 OMS [#62](https://github.com/avysotsky/TradeOps-v2/pull/62) | `814f7ea5c470f01e87719a75b778a1b5226a7d69` | .NET 10 build, OMS/unit tests, Mock+PG smoke |
| F1 Deribit [#63](https://github.com/avysotsky/TradeOps-v2/pull/63) | `246bd259a87958338ab56f9dc705224b0ed2b074` | Node 24, existing npm tests, **explicit** VAN test, Vite build |
| F2 CoinCall [#64](https://github.com/avysotsky/TradeOps-v2/pull/64) | `4322c8aaed09d76c3b3a95d3784d2676613139de` | Node 24, `npm run check` |

These are inspection-time SHAs, **not permanent assertions**. Always query the live HEAD before dispatch and never reuse results after the PR moves. The CI branch starts from `main@c7a0d290...`; it deliberately does **not** contain either React application or the B1 OMS contracts.

## 2. GitHub Actions design and runbook

### Default `build.yml`

- `push` on `main` and `TradeOps/**`, `pull_request` targeting `main`, and a reusable `workflow_call(target_sha)`.
- `actions/checkout@v4` checks out the actual PR head or supplied immutable SHA (not the synthetic merge ref); a hard assertion compares `git rev-parse HEAD` to the selected SHA.
- `actions/setup-dotnet@v4` installs SDK from **`global.json`: 10.0.401**, rather than the previous hardcoded SDK 8. Includes `dotnet --info`, solution restore/build, `dotnet test` with TRX results, and the **unchanged** existing API+PostgreSQL Mock-only smoke assertions and Docker/deployment checks.
- `Exchange__Provider=Mock`, `DeribitTerminal__ExecutionEnabled=false`, `DeribitTerminal__MainnetExecutionEnabled=false` and Telegram disabled. Database mutation, signals and cancel assertions are permitted only in isolated local CI PostgreSQL with the Mock exchange.
- An always-attempted evidence artifact retains source SHA, SDK info and test TRX (14 days). Test failures are not swallowed. Full smoke outcomes are visible by named GitHub job steps; a skipped or failed step does **not** constitute a PASS.

### `exact-sha-acceptance.yml`

This workflow is **manually dispatched after it has been accepted into the default branch**. It deliberately does not falsely promise that a newly introduced workflow on a PR branch can automatically run on stacked PRs that have not inherited it.

1. Open **Actions → exact-sha-acceptance → Run workflow** on `main`.
2. Choose **62**, **63** or **64** and paste the current full **40-character lowercase HEAD SHA** from that PR.
3. The `validate` job checks allowed PR number, current open PR state, same-repo source, exact current HEAD equality and SHA syntax through GitHub API. Mismatch **fails closed**.
4. For B1, call reusable `build.yml` against that SHA (including existing PostgreSQL smoke). For F1, require `apps/trade-terminal` and execute `npm test`, `node --test tests/vanLayout.test.mjs`, `npm run build`. For F2, require `apps/coincall-terminal` and execute `npm run check`.
5. Both frontend jobs use Node **24**. With `package-lock.json` use `npm ci`; without it, use `npm install --no-package-lock --no-audit --no-fund`, explicitly record **reproducibility limited** in the artifact and job summary. This is not a pinned dependency qualification.
6. The final `finalize` job rejects failures, selected-job skips, and a PR HEAD that changed during verification. No missing required app path may be skipped as success.
7. Record the exact tested SHA, GitHub run ID/attempt, steps and downloadable evidence. If the PR SHA changes, rerun.

**Gate specificity:** React paths missing from merged `main` are **NOT RUN / unavailable** on that baseline. The designated F1/F2 gate requires its app and fails if missing. PR #63 is stacked on the #55 ancestry, so testing it by its own commit SHA includes that ancestry; this does **not** approve or merge the predecessor chain.

**Visual scope:** builds and source-level tests do not certify 1440×900 or 390×844 browser screenshot parity. Golden-browser acceptance remains **NOT RUN** until independent snapshots are actually captured and compared.

## 3. Verified GitHub Actions run gap / operational blocker

The accessible GitHub Actions runs endpoint returned **51 historical runs**; its latest entry is `build` run [37946112254](https://github.com/avysotsky/TradeOps-v2/actions/runs/37946112254), `2026-10-09T14:41:53Z`, on `main@30e821f93611d3dc771a358090b8d502291b5018` (success). As of **2026-10-25**, no newer runs were returned despite later commits to `main` and `TradeOps/**`. The commit-run/status lookups for the current `main`, B1/F1/F2 HEADs and new CI commits returned **zero associated run results/statuses**. A 9 October success is NOT evidence that 25 October heads pass.

**What is established:** `build.yml` has `push` triggers for `main` and `TradeOps/**` and the run gap spans eligible branch updates; SDK 8 versus net10.0 would explain a failed *started* build, but by itself cannot explain the absence of a workflow run record. PR branch-base filtering prevents some stacked PR runs; it does not explain missing eligible push runs.

**Root cause remains unverified.** The linked GitHub API surface provided run listings but does not authorize the repository `/actions/permissions` endpoint; therefore this worker cannot distinguish conclusively between repository/owner-level Actions disabled, workflow disabled, Actions billing/quota restriction, policy restrictions and runner dispatch/service problems. No logs exist for missing runs, and no fabricated PASS is reported.

**Owner action, minimum UI investigation:**

- Repository **Settings → Actions → General**: confirm Actions are enabled/allowed for this repository; check inherited account/organization policy and that `actions/checkout`, `actions/setup-dotnet`, `actions/setup-node`, `actions/upload-artifact` and local reusable workflows are permitted.
- Repository **Actions → Workflows → build**: confirm the workflow is enabled, rather than showing an **Enable workflow** action/banner.
- Verify **Settings → Billing / Plans and usage → Actions** availability/limits and whether jobs are queued without an eligible hosted runner; check recent workflow activity/runs and any GitHub-wide incident.
- After owner resolves the restriction, make an authorized new commit/dispatch and compare its **run ID, event, head SHA, runner outcome** to the target. Do not confuse an old green check with a current one.
- Until this exact-SHA acceptance workflow lands on default branch, its **Run workflow** button may not appear. Do not merge merely to force a run; orchestration approval is required.

## 4. Non-destructive local fallback (WSL/Linux)

Prerequisites: authenticated Git access to the private repo, .NET SDK **10.0.401** for B1 or Node **24** and npm for F1/F2, package-download access, and a clean external disk location for the temporary detached worktree. No live keys or exchange access required.

From the root of the CI-01 branch checkout, for example in WSL:

```bash
bash scripts/ci/verify-exact-sha.sh 62 814f7ea5c470f01e87719a75b778a1b5226a7d69 /mnt/d/Projects/TradeOps-v2
bash scripts/ci/verify-exact-sha.sh 63 246bd259a87958338ab56f9dc705224b0ed2b074 /mnt/d/Projects/TradeOps-v2
bash scripts/ci/verify-exact-sha.sh 64 4322c8aaed09d76c3b3a95d3784d2676613139de /mnt/d/Projects/TradeOps-v2
```

The helper first compares the supplied SHA to the actual **remote worker branch HEAD** via `git ls-remote`, fetches the commit, creates a disposable detached worktree, asserts `git rev-parse HEAD`, runs the selected native SDK/test/build commands, then removes the worktree. It does not reset another worker's branch or rewrite `main`. If Git credentials, packages or runtime are unavailable, the command **fails**, not PASS. Local backend fallback validates compile and xUnit, but reports **PostgreSQL Mock smoke NOT RUN**: that full assertion sequence is retained in GitHub `build.yml` and requires a service-backed runner. Local fallback also does not create a GitHub check run.

For PowerShell, invoke the script through WSL, adapting your checkout path:

```powershell
wsl.exe -- bash -lc 'cd /mnt/d/Projects/TradeOps-v2 && bash scripts/ci/verify-exact-sha.sh 62 814f7ea5c470f01e87719a75b778a1b5226a7d69 .'
```

## 5. Evidence / qualification ledger (2026-10-25)

| Requirement | Evidence now | Status |
| --- | --- | --- |
| Issue/G0/roadmap and target SHA discovery | Connected GitHub issue/PR/ref and source inspection | **PASS (metadata only)** |
| SDK alignment / exact checkout / workflow changes committed | `build.yml`, `exact-sha-acceptance.yml` source changes | **IMPLEMENTED; runtime NOT RUN** |
| Backend `dotnet restore/build/test` at B1 HEAD | .NET SDK not present in this worker execution environment; no Actions run | **NOT RUN** |
| Existing Mock+PostgreSQL smoke at B1 HEAD | Existing workflow assertions preserved, but no hosted run | **NOT RUN** |
| F1 `npm test`, explicit VAN and Vite build at PR #63 HEAD | No Node 24 + npm dependency installation against private PR source in worker runtime | **NOT RUN** |
| F2 `npm run check` at PR #64 HEAD | Same constraints, no matching GitHub Actions run | **NOT RUN** |
| Actual pixel-diff browser screenshot parity | No browser golden baseline or captured screenshot | **NOT RUN** |
| Explanation of missing Actions executions | Gap proven; disabled/policy/billing/runners not inspectable via connected permissions | **BLOCKED: owner UI inspection** |
| Mainnet execution/read-only | No execution flags enabled, no credentials accessed or real orders sent | **UNCHANGED** |

**Acceptance:** this is a Draft PR. G0 and the orchestrator must independently review workflow security, exact SHA, tests and final statuses before any merge. Never promote missing evidence to success; no automatic mainnet order entry.
