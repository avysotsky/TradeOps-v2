# CI-01 — Local WSL2 / Docker exact-SHA acceptance

**Canonical spec:** [Issue #65](https://github.com/avysotsky/TradeOps-v2/issues/65). **Draft PR:** [#66](https://github.com/avysotsky/TradeOps-v2/pull/66). Acceptance and merge belong to orchestrator.

**GitHub Actions is PROHIBITED.** This work neither changes nor runs it; GitHub is used solely for source refs and Draft PR review.

## Bootstrap from Draft PR #66 (when scripts are absent locally)

Because this is a **Draft PR**, the launcher exists on the CI-01 GitHub branch but might not exist in the owner's current local checkout. A PowerShell `CommandNotFoundException` at `D:\\Projects\\TradeOps-v2\\scripts\\ci\\Run-TradeOpsAcceptance.ps1` means the local script was never invoked. **Do not merge or switch the running repo branch to fix this.**

Copy the following **single PowerShell block**. It fetches the CI-01 source to a remote-tracking ref without writing `FETCH_HEAD`, creates a uniquely named detached *temporary* Git worktree, runs the 3-PR Python orchestrator directly inside WSL (bypassing both PowerShell execution policy and Bash CRLF conversion), pointing it at the original repository for source-ref discovery, then removes only that temporary worktree:

```powershell
$ErrorActionPreference = 'Stop'
$repo = 'D:\\Projects\\TradeOps-v2'
$branch = 'TradeOps/ci-01-exact-sha-dotnet10-react-gates'
$tempWorktree = Join-Path $env:TEMP ('TradeOps-CI01-' + [guid]::NewGuid().ToString('N'))
if (-not (Test-Path -LiteralPath $repo)) { throw "Missing repository: $repo" }
git.exe -C $repo fetch --no-tags --no-write-fetch-head origin "refs/heads/${branch}:refs/remotes/origin/${branch}"
if ($LASTEXITCODE -ne 0) { throw 'CI-01 branch fetch failed' }
git.exe -C $repo worktree add --detach $tempWorktree "refs/remotes/origin/$branch"
if ($LASTEXITCODE -ne 0) { throw 'Temporary CI-01 worktree creation failed' }
try {
    $scriptWin = Join-Path $tempWorktree 'scripts\\ci\\local_acceptance.py'
    $wslScript = (& wsl.exe --exec wslpath -u $scriptWin).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Cannot translate Python script path into WSL' }
    $wslRepo = (& wsl.exe --exec wslpath -u $repo).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Cannot translate repository path into WSL' }
    & wsl.exe --exec python3 $wslScript all $wslRepo
    if ($LASTEXITCODE -ne 0) { throw "Acceptance returned exit $LASTEXITCODE; consult WSL summary.json files" }
}
finally {
    git.exe -C $repo worktree remove --force $tempWorktree
    if ($LASTEXITCODE -ne 0) { Write-Warning "Remove only the temporary CI-01 worktree manually: $tempWorktree" }
}
```

This fetch/worktree operation does **not** switch or reset any active branch and does not modify any ResidentChat worktree. Subsequent source checks and Docker runs are performed by the launcher. This bootstrap does not execute `.ps1` or `.sh` files, so neither PowerShell execution policy nor Windows Git's CRLF conversions can prevent it from starting.

## Windows entrypoint when the CI-01 files already exist locally

Once this PR branch's scripts are accessible at `D:\Projects\TradeOps-v2`, open PowerShell:

```powershell
& 'D:\Projects\TradeOps-v2\scripts\ci\Run-TradeOpsAcceptance.ps1'
```

Where PowerShell execution policy forbids `.ps1` (as on the owner's machine), invoke WSL Python directly once the files are locally present:

```powershell
wsl.exe --exec python3 /mnt/d/Projects/TradeOps-v2/scripts/ci/local_acceptance.py all /mnt/d/Projects/TradeOps-v2
```

For another checkout path, pass `-Repository 'D:\your\path\TradeOps-v2'` to the launcher. In Ubuntu, use `python3 scripts/ci/local_acceptance.py all /mnt/d/Projects/TradeOps-v2`. Individual checks accept `62`, `63`, `64`, or `70` instead of `all`; `frontends` runs 63/64. The legacy `all` mode remains 62/63/64. **Do not switch any active TradeOps/ResidentChat branch to run this tool**; use a separate checkout containing the CI-01 scripts. The Python driver requires host WSL `git`, `python3`, Docker daemon access and working GitHub Git credentials. It **never requires .NET or Node installed in host Ubuntu**.

The repository also includes `scripts/ci/.gitattributes` forcing LF for `.sh` and `.py` files, including with Windows Git `core.autocrlf=true`. For the immediate operator recovery, direct Python is the more robust path.

## WSL private GitHub access

The remote SHA check is done by WSL Git first. If WSL Git cannot authenticate to the private repository (or the user has no WSL credential helper), the verifier now invokes the **existing Windows `git.exe` from WSL** using `wslpath -w`. This reuses Windows Git's existing credentials through the standard Git process; it **does not copy credentials** to files or alter global Git configuration. It fetches the selected immutable SHA using the same authenticated tool and then validates its detached Linux worktree.

The `git_transport` (`wsl-git` or `windows-git`) is recorded per PR in `summary.json`. If neither method works, the local console and `notes` contain both process exit codes and sanitized Git errors instead of hiding the reason behind a generic `BLOCKED_ENV`.

For direct diagnosis before rerunning all 3 PRs, the owner may execute this standalone, non-mutating WSL command from PowerShell:

```powershell
wsl.exe --exec env GIT_TERMINAL_PROMPT=0 git -C /mnt/d/Projects/TradeOps-v2 ls-remote --exit-code origin refs/pull/62/head refs/heads/TradeOps/b1-01-oms-domain-identity-contracts
```

If WSL Git reports an authentication failure, do not put passwords or access tokens in the console or repository URL. The CI-01 Windows Git bridge is the preferred automatic fallback.

## Source verification and isolation

For each run, the driver calls `git ls-remote origin` against **both** the actual `refs/pull/<number>/head` and the designated source `refs/heads/TradeOps/...`. The two SHAs must be equal, and the repo `origin` must be the canonical `github.com/avysotsky/TradeOps-v2`. It fetches **that exact current SHA**, creates a disposable detached `git worktree` and asserts `git rev-parse HEAD`. At the end it repeats both remote-ref checks. Any intervening head movement produces **STALE_SHA**. No `git reset`, checkout of an active branch, stashing, branch mutation or reuse of another worker's temporary files.

Source SHA snapshots from the initial issue (never automatically assumed as current): B1 #62 `814f7ea5c470f01e87719a75b778a1b5226a7d69`; F1 #63 `246bd259a87958338ab56f9dc705224b0ed2b074`; F2 #64 `4322c8aaed09d76c3b3a95d3784d2676613139de`.

**F1 #63 is stacked onto #55.** A passing F1 test validates its entire checked-out source tree; it does not approve or merge any unmerged ancestor.

## Issue #71 — B2 durable OMS real PostgreSQL 17 acceptance (PR #70)

Target **`70`** is part of the existing `scripts/ci/local_acceptance.py` runner. It validates live GitHub `refs/pull/70/head` against `refs/heads/TradeOps/b2-01-durable-command-journal`, fetches the exact SHA and tests in a temporary detached Linux worktree, then rechecks the remote SHA. The supplied B2 handoff SHA `2a96c52b1ec0e5ef425bd87a575f457b794864ab` is a snapshot, **not an enforced stale target**. B2 is stacked on B1 #62. Legacy `all` remains 62/63/64 and `frontends` remains 63/64.

**Owner launcher:** use the same temporary-worktree PowerShell block as above but run `& wsl.exe --exec python3 $wslScript 70 $wslRepo` instead of `all`. The ChatGPT handoff contains the full copy/paste block; it does not execute `.ps1` or `.sh` from Windows.

### B2-specific fail-closed gate

- Docker .NET SDK **10.0.401**, restore (`dotnet restore TradeOps.sln`) with dependency network, followed by Release build (`dotnet build TradeOps.sln -c Release --no-restore`) without a network. SDK version must equal source `global.json`.
- Creates only a uniquely named `ci01-b2-*` **internal** Docker network and temporary `postgres:17.6-bookworm` server on **tmpfs**, with no host ports, shared databases or persistent volumes; checks `pg_isready` and actual PostgreSQL `server_version_num` (170000..179999).
- Injects `TRADEOPS_B2_POSTGRES` only into the disposable .NET test containers, using a randomly generated PostgreSQL admin password. The PostgreSQL test fixture creates and destroys its own uniquely named test databases; no ResidentChat/JobRadar or persistent TradeOps DB changes.
- Runs `dotnet test tests/TradeOps.UnitTests/TradeOps.UnitTests.csproj --filter FullyQualifiedName~OmsJournalPostgresTests` (requires **>=19 real passes**), the `OmsJournalContractTests` filter (requires **>=5 real passes**), then full `dotnet test TradeOps.sln` (requires **>=626 passes**: 602 previously proven B1 tests + 19 B2 PostgreSQL + 5 B2 contract tests). The same PG17 environment is available throughout, so missing env does not silently bypass integration cases.
- Dedicated TRX per suite, with fail-closed parsing. Each must have valid parsed counters, `total == passed`, `failed=0`, `skipped=0`, and pass minimums. Migration Up, guarded Down, empty Down, CAS, races, collision and crash-boundary behaviour are covered by the existing B2 tests. This does **not** claim an actual SIGKILL harness.
- Forces `Exchange__Provider=Mock`, `DeribitTerminal__ExecutionEnabled=false` and `DeribitTerminal__MainnetExecutionEnabled=false`; test containers connect solely to the internal Docker network, not an exchange. Cleanup removes **only** the B2-created PG container and network; failures prevent PASS.

Evidence: `~/tradeops-acceptance-results/pr-70/<exact-sha>/<run-id>/summary.json`, `b2-restore.log`, `b2-build.log`, `b2-postgres-version.log`, `b2-postgres-test.log`, `b2-contracts-test.log`, `b2-full-test.log`, `trx/postgres/*.trx`, `trx/contracts/*.trx`, `trx/full/*.trx`, private postgres service/cleanup logs and a dated aggregate JSON. `b2_suites.postgres`, `.contracts` and `.full` contain individual counts and statuses. Overall `test_counts.total` aggregates the 3 invocations (targeted cases appear again in the full test suite).

### Owner-local B2 acceptance — PASS (2026-10-25)

The owner ran CI-01 runner HEAD `b32e662633920fc5853725e008d70deacf5c673a` via Windows PowerShell → WSL2 Ubuntu Docker. All **13/13 CI-01 runner self-tests passed**. PR #70's checked live source HEAD was `2a96c52b1ec0e5ef425bd87a575f457b794864ab` (still current during independent GitHub review). Runner output:

| Suite / step | Observed result |
| --- | --- |
| .NET 10 version, restore, Release build | Every required command exited `0` |
| Temporary PostgreSQL17 startup, readiness and version check | Every required command exited `0` |
| `OmsJournalPostgresTests` | PASS; at least 19/19 under fail-closed gate |
| `OmsJournalContractTests` | PASS; at least 5/5 under fail-closed gate |
| Full backend test suite | PASS; at least 626/626 under fail-closed gate |
| Combined 3 TRX suites | **650/650 passed, 0 failed, 0 skipped; TRX parse errors []** |
| Overall CI-01 B2 runner exit | **0 / PASS**, validated for the above exact B2 SHA |

The per-suite numbers (19, 5, 626) are **deduced from the 650 aggregate total and the three independent minimum-pass thresholds**, not from separately displayed TRX rows. The owner's console confirms that all three test processes exited 0, all three TRX suites parsed, and the fail-closed status was PASS. The raw TRX/XML and full `summary.json` remain on the owner's WSL2 filesystem; the CI-01 worker did **not** open or independently inspect those local files.

Aggregate evidence: `/home/user/tradeops-acceptance-results/acceptance-20261025T184923Z-b000f7.json`

Per-run evidence: `/home/user/tradeops-acceptance-results/pr-70/2a96c52b1ec0e5ef425bd87a575f457b794864ab/20261025T184923Z-b000f7/summary.json`. Log filenames and distinct TRX directories are documented above.

**Status: Automated B2 local acceptance PASS at the tested exact SHA; orchestrator inspection/sign-off pending.** This does not prove process-SIGKILL testing, trading execution authorization or institutional OMS readiness. No GitHub Actions, live exchange traffic, merge or resident-service mutation.

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

## Owner-local acceptance observation — 2026-10-25

First full owner WSL2/Docker run: aggregate `acceptance-20261025T165017Z-e9c46a.json`. These source SHAs were resolved from live remote HEADs, not hardcoded snapshots:

| Gate | SHA tested | Build/test exit codes | Recorded status |
| --- | --- | --- | --- |
| B1 #62 | `a799aec77ef498c88977a3ee6466989c1a0958ee` | .NET restore/build/tests and PG17 Mock smoke returned 0; TRX **602 passed, 0 failed** | **PASS** |
| F1 #63 | `e1b7c276c2bdce08de9467f382ea339c46862f83` | npm test, explicit `vanLayout.test.mjs`, React build all exited 0 | **FAIL** in original run: `TAP totals unavailable` |
| F2 #64 | `9261e94efc66aa935d273379ea668199321adfd4` | npm test, React build both exited 0 | **FAIL** in original run: `TAP totals unavailable` |

**Parser correction:** Node.js emits test totals in TAP (`# tests 4`) or the human-readable spec reporter (`ℹ tests 4`). Previously the parser accepted TAP **only**, causing a false negative where shell commands exited successfully but counts remained zero. It now parses **both**, handles ANSI escape codes, records per-log totals and fails closed if required suite summaries remain unavailable. Local source-only parser tests include TAP, spec, ANSI, malformed/missing test summaries, and Deribit two-suite integrity; these do **not** replace an owner-local rerun. The original FAIL records must not be overwritten or retroactively declared PASS without parsing their saved logs.

### Successful owner-local React rerun (run 20261025T170622Z-1af70d)

The owner ran `frontends` using CI-01 source HEAD `6f39ecfc2e61bc9d3c4236d7f91def27cacbdc43` after the Node reporter fix. Both frontends completed with **PASS**, and each required command exited 0.

| Gate | Verified PR HEAD | Results | Evidence |
| --- | --- | --- | --- |
| F1 #63 | `e1b7c276c2bdce08de9467f382ea339c46862f83` | **PASS: 42/42**, 0 failed, 0 skipped. `npm test` 33/33; explicit `vanLayout.test.mjs` 9/9; React build exit 0 | `pr-63/<sha>/20261025T170622Z-1af70d/summary.json` |
| F2 #64 | `9261e94efc66aa935d273379ea668199321adfd4` | **PASS: 7/7**, 0 failed, 0 skipped. `npm test` and React build exit 0 | `pr-64/<sha>/20261025T170622Z-1af70d/summary.json` |

Aggregate result: `~/tradeops-acceptance-results/acceptance-20261025T170622Z-1af70d.json`, runner exit **0**.

**Final local command-gate tally across the two owner runs:** B1 #62 **602/602 PASS** (PostgreSQL17 Mock smoke successful); F1 #63 **42/42 PASS**; F2 #64 **7/7 PASS**. Total **651 tests passed; 0 failed, 0 skipped**. Both original frontend FAIL reports from the first parser version remain stored and are superseded by the separate successful rerun, **not overwritten**.

At the time of the evidence update, the GitHub PR HEADs for #62/#63/#64 still matched the tested SHAs. This verifies local automated command gates, **not** screenshot parity in a real browser. Visual comparisons remain outstanding for F1 at 1440×900 and 390×844, and F2 at 1440×900 and 768×900. Until separately verified, report browser acceptance as **NOT RUN**.

Source-only harness tests are independent of these PR acceptance counts: the current file `scripts/ci/test_local_acceptance.py` contains 10 cases, but no owner-local run of this harness was supplied with these logs. Do not conflate the source harness checks with 651 application tests.

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
- `cd scripts/ci && python3 -m unittest -v test_local_acceptance`: optional standalone check of CI-01 orchestration logic (10 test methods in current source; no owner-local execution evidence included).
- Authoring environment does not have access to owner's WSL2, but **actual owner console logs now confirm the .NET, PostgreSQL17 Mock and frontend Docker gates above as PASS on the exact PR HEADs**. Browser screenshot comparisons are still **NOT RUN**. The full logs and `summary.json` files remain stored on the owner's machine, not in GitHub.
- GitHub workflow diff must remain **empty**: `build.yml` restored **byte-for-byte** to `main`, new workflow removed. No GitHub Actions run is requested, expected or required.

Draft PR #66 stays unmerged until orchestrator reviews scope, SHA proofs and real owner-local acceptance evidence.
