# CI-01 — Local WSL2 / Docker exact-SHA acceptance

**Canonical spec:** [Issue #65](https://github.com/avysotsky/TradeOps-v2/issues/65). **Draft PR:** [#66](https://github.com/avysotsky/TradeOps-v2/pull/66). Acceptance and merge belong to orchestrator.

**GitHub Actions is PROHIBITED.** This work neither changes nor runs it; GitHub is used solely for source refs and Draft PR review.

## One-command Windows entrypoint

Once this PR branch's scripts are accessible at `D:\Projects\TradeOps-v2`, open PowerShell:

```powershell
& 'D:\Projects\TradeOps-v2\scripts\ci\Run-TradeOpsAcceptance.ps1'
```

If script execution policy prevents a `.ps1` launcher, invoke WSL2 directly with **one** PowerShell command:

```powershell
wsl.exe --exec bash /mnt/d/Projects/TradeOps-v2/scripts/ci/verify-exact-sha.sh all /mnt/d/Projects/TradeOps-v2
```

For another checkout path, pass `-Repository 'D:\your\path\TradeOps-v2'` to the launcher. In Ubuntu, use `bash scripts/ci/verify-exact-sha.sh all /mnt/d/Projects/TradeOps-v2`. Individual checks accept `62`, `63` or `64` instead of `all`. **Do not switch any active TradeOps/ResidentChat branch to run this tool**; use a separate checkout containing the CI-01 scripts. The Python driver requires host WSL `git`, `python3`, Docker daemon access and working GitHub Git credentials. It **never requires .NET or Node installed in host Ubuntu**.

## Source verification and isolation

For each run, the driver calls `git ls-remote origin` against **both** the actual `refs/pull/<number>/head` and the designated source `refs/heads/TradeOps/...`. The two SHAs must be equal, and the repo `origin` must be the canonical `github.com/avysotsky/TradeOps-v2`. It fetches **that exact current SHA**, creates a disposable detached `git worktree` and asserts `git rev-parse HEAD`. At the end it repeats both remote-ref checks. Any intervening head movement produces **STALE_SHA**. No `git reset`, checkout of an active branch, stashing, branch mutation or reuse of another worker's temporary files.

Source SHA snapshots from the initial issue (never automatically assumed as current): B1 #62 `814f7ea5c470f01e87719a75b778a1b5226a7d69`; F1 #63 `246bd259a87958338ab56f9dc705224b0ed2b074`; F2 #64 `4322c8aaed09d76c3b3a95d3784d2676613139de`.

**F1 #63 is stacked onto #55.** A passing F1 test validates its entire checked-out source tree; it does not approve or merge any unmerged ancestor.

## Docker matrix

| PR/gate | Container and commands | Requirements |
| --- | --- | --- |
| B1 #62 | .NET SDK 10.0.401, `dotnet --info`, `dotnet restore TradeOps.sln`, `dotnet build TradeOps.sln -c Release --no-restore`, `dotnet test TradeOps.sln -c Release --no-build --no-restore` | Existing backend tests **and** OMS contracts; SDK must equal target `global.json`; capture TRX/test counts |
| B1 isolated Mock smoke | One disposable PostgreSQL **17**, one .NET API container, one private bridge network, curl probe | PostgreSQL `pg_isready`; `/health/live`, `/health/ready` (PostgreSQL-ready assertion), OpenAPI JSON; one **synthetic Mock-only** signal with PartiallyFilled/0.0006 assertion |
| F1 #63 | Node **24**, `npm test`, explicit `node --test tests/vanLayout.test.mjs`, `npm run build` | Existing RAW L2/clock regressions; VAN source-regex test does **not** qualify screenshot parity |
| F2 #64 | Node **24**, `npm test`, `npm run build` | Equivalent to `npm run check`, reports test and build separately |

### Image pinning and dependencies

- .NET SDK: `mcr.microsoft.com/dotnet/sdk:10.0.401-noble@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317` — **digest pinned**.
- Node: `node:24.12.0-bookworm-slim` — fixed version tag, **not** digest pinned.
- PostgreSQL: `postgres:17.6-bookworm` — fixed version tag, **not** digest pinned.
- curl: `curlimages/curl:8.12.1` — fixed version tag, **not** digest pinned.

The runner records the actual `docker image inspect` ID and RepoDigests per image. Operator-reviewed immutable images can be configured by `CI01_DOTNET_IMAGE`, `CI01_NODE_IMAGE`, `CI01_POSTGRES_IMAGE` and `CI01_CURL_IMAGE`. Unavailable images or dependencies produce a documented failure/blocker, **never PASS**. NuGet/npm caches and source worktrees are ephemeral and live under `~/tradeops-acceptance-results/.scratch` on the WSL Linux filesystem (not NTFS); only restore/package install have bridge networking, while tests/builds default to `--network none`.

F1 and F2 lack lockfiles at the inspected heads; the runner uses `npm install --no-package-lock` and records `dependency_reproducibility=LIMITED_NO_LOCKFILE`. If future worker changes add lockfiles, the runner uses `npm ci`. Real dependency resolution remains less reproducible until frontend workers add committed lockfiles.

### No impact on ResidentChat / other Docker services

The Mock smoke starts only unique `ci01-<random>-pg`, `ci01-<random>-api` and `ci01-<random>-net` resources, with PostgreSQL database files on **temporary container tmpfs**. The runner publishes **zero host ports**; only the private Docker network connects the API, database and probe. It never calls `docker system prune`, `docker compose down`, stops existing containers, changes resident services, reuses volumes or creates a shared PostgreSQL database. Cleanup targets **only the names created by this run**, including on exceptions.

All runner containers explicitly receive `Exchange__Provider=Mock`, `Telegram__Enabled=false`, `DeribitTerminal__ExecutionEnabled=false`, `DeribitTerminal__MainnetExecutionEnabled=false`; they do **not** import host exchange secrets, `.env` files, tokens, DPAPI or live broker credentials. The synthetic signal goes exclusively to the disposable local Mock API. No real trading operations.

## Evidence

Default root: `~/tradeops-acceptance-results/` (not tracked by GitHub). Example structure:

```text
pr-62/<exact-sha>/summary.json
pr-62/<exact-sha>/dotnet-version.log, dotnet-info.log, restore.log, build.log, dotnet-test.log
pr-62/<exact-sha>/trx/*.trx
pr-62/<exact-sha>/api-health-live.log, api-health-ready.log, api-openapi.log, mock-order.log
pr-63/<exact-sha>/summary.json, npm-test.log, van-layout.log, react-build.log
pr-64/<exact-sha>/summary.json, npm-test.log, react-build.log
acceptance-<utc-time>-<run-id>.json
```

On repeated runs of the same SHA, artifacts go under `pr-<number>/<sha>/<run-id>/` to preserve previous records. `summary.json` contains PR, gate, source SHA, checkout SHA, remote SHAs before/after, timestamps, status, individual step command names/exit codes/log names, test totals/passes/failures/skips (TRX or Node TAP), versions, image IDs and digests, npm lockfile state and reproducibility warning, PostgreSQL smoke outcome and failure notes. The aggregate JSON references the three individual summaries. Results are local, never auto-uploaded. Don't publish sensitive logs or environment variables.

Possible statuses: `PASS`, `FAIL`, `NOT_RUN`, `STALE_SHA`, `BLOCKED_ENV`. **PASS is only the listed local build/test/Mock contract suite**, not OMS institutional readiness or browser visual parity. Browser screenshot comparisons remain independently **NOT RUN**: F1 viewports 1440×900 / 390×844 and F2 1440×900 / 768×900.

## Worker verification and current limitations (2026-10-25)

- `python3 -m py_compile scripts/ci/{local_acceptance,test_local_acceptance}.py` and `bash -n scripts/ci/verify-exact-sha.sh`: syntax validation only.
- `cd scripts/ci && python3 -m unittest -v test_local_acceptance`: orchestration logic unit tests, **5/5 PASS** in authoring environment.
- No access to owner's WSL2; this authoring environment has **no Docker binary**. B1 .NET build, OMS tests, full backend tests, PostgreSQL Mock smoke, F1/F2 npm tests and React builds and screenshot comparison: **NOT RUN**. Tests cannot be declared successful from source review.
- GitHub workflow diff must remain **empty**: `build.yml` restored **byte-for-byte** to `main`, new workflow removed. No GitHub Actions run is requested, expected or required.

Draft PR #66 stays unmerged until orchestrator reviews scope, SHA proofs and real owner-local acceptance evidence.
