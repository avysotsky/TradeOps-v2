# TO-V2-14 — Real PostgreSQL 17 + real TradeOps API/Worker acceptance

**Purpose:** close the gap between autonomous WSL2 build/unit qualification
and *actual* PostgreSQL/runtime verification. This is not just a TestServer
or EF Core InMemory test: it starts PostgreSQL 17, real .NET 10 console
executables, the real ASP.NET Core API, and the real Worker process.

**Security:** Mock exchange only; IBKR read-only demo uses synthetic
transport; **no real IBKR account, TWS/Gateway, broker authentication,
order placement or cancellation**. No production PostgreSQL.

## Requirements

- Trusted **user-owned WSL2 Ubuntu** shell with Python 3, curl,
  flock, Git, Docker Engine and a locally cached
  `postgres:17-bookworm` Docker image. The interactive host's `dotnet`
  command is **optional**. If .NET 10 SDK is absent from WSL `PATH`,
  the script automatically uses one of the already cached SDK images,
  preferring `jobradar-pr-autotest:dotnet10`, then
  `mcr.microsoft.com/dotnet/sdk:10.0`. These are checked for SDK 10;
  no image pull or software installation is attempted. In container
  mode, **only the dependency restore** may reach the public NuGet
  feed, constrained by the dedicated
  `scripts/ibkr/to-v2-14-nuget.config` (`nuget.org` exclusively).
  Restore uses a **per-run temporary NuGet package cache** shared
  between the disposable SDK containers that perform restore and
  build. Build and the account-free demo run with Docker networking
  disabled; the real API, Worker and journal smoke share only the
  local Docker host namespace to reach the isolated PostgreSQL
  container. All application exchange providers stay configured
  as `Mock` with external integrations disabled.
- A **clean, detached checkout** of TO-V2-14. Use a separate `git
  worktree` instead of switching the running relay's checkout.
- Permission to start and remove a uniquely named ephemeral Docker
  container on the user's own machine. Existing WSL2 services, runner
  locks, other PostgreSQL databases, databases, roles, and containers
  must not be altered.
- No real secrets: the script creates temporary random database and
  API auth credentials solely for the disposable local test.

## Single WSL command

From the detached checkout at repository root:

```bash
bash scripts/ibkr/to-v2-14-run-real-postgres-acceptance.sh "<verified-40-character-Git-HEAD>"
```

The script independently checks all tools, .NET 10 and the locally
cached PostgreSQL 17 image. It builds `TradeOps.sln` (no exchange
connections), starts a CPU/memory-limited PostgreSQL 17 container bound
only to `127.0.0.1` on a random port and creates two isolated databases:

1. `tradeops_ibkr_offline_smoke_<random>`: the **existing, real**
   `TradeOps.IbkrOfflinePostgresSmoke` binary. Its actual Npgsql
   transactions exercise:
   - immutable intent, unique client order ID;
   - preflight, submission-started before crash, persistent ACK;
   - repeated execution IDs, conflicting IDs, rollback;
   - two concurrent independent PostgreSQL writers and lock ordering;
   - durable recovery after independent journal instances;
   - durable reconciliation + baseline import + atomic lifecycle entry;
   - cross-order execution-ID collision, observation rollback;
   - isolated schema removal and final zero broker requests.
2. `tradeops_api_smoke_<random>`: the **real TradeOps API**.
   It uses actual EF Core PostgreSQL migrations, never SQLite/InMemory.
   It binds HTTP to `127.0.0.1`, runs with `Exchange:Provider=Mock`,
   requires synthetic random API keys for signal/operator operations,
   and disables Telegram and TradingView edge connectivity.
   Real HTTP tests confirm:
   - `/health/live` and `/health/ready` with a real PostgreSQL dependency;
   - unauthorized signal request → HTTP 401;
   - synthetic signal → HTTP 200 and one persisted mock order;
   - exact duplicate signal → same client order ID, no new order;
   - conflicting request for the same signal ID → HTTP 409;
   - persisted lifecycle history and operator authorization;
   - authorized `/api/system/reconcile` → `Filled` in DB;
   - full API process termination and restart with **the same database**;
   - reading the persisted signal, filled history and idempotency after restart;
   - independent SQL counts in `"Orders"` and
     `"OrderLifecycleEvents"` from the live PostgreSQL container.

Additionally, the script **runs**, not merely builds,
`TradeOps.IbkrOfflineDemo` against account-free synthetic read-only
HTTP handlers, and starts the real TradeOps Worker with the same
disposable database/Mock adapter to check healthy startup.

The entire PostgreSQL container is removed in an exit trap;
temporary authentication data and logs are deleted. If teardown
fails, the run is **FAIL**, not PASS. Each final stdout marker
includes the exact `git rev-parse HEAD` and states
`broker_requests=0`.

## Boundaries of evidence

- The existing autonomous WSL2 runner is **not modified**. Its
  restore/build/test + six cleanup PASS does **not** establish the real
  PostgreSQL acceptance has executed.
- The PostgreSQL run is **NOT RUN** until the operator executes this
  exact script in their trusted WSL2 shell and supplies the final
  clean marker and exit code. Never infer a runtime PASS from source
  code, GitHub commits or a Draft PR.
- The script does not test real IBKR trading, live market data, IBKR
  account sessions, network outages to an actual exchange, real
  money, or Windows GUI applications. Those are separately gated.
- The API/Worker tests use an actual PostgreSQL process but a
  simulated broker. They do not constitute certification for
  execution-capable non-mock adapters.
- No migrations or schema changes touch the owner's existing DB.
  No scheduled actions, GitHub Actions, new WSL services, CI runner
  modifications, merge, or NuGet publication.

## Evidence classification

| Gate | Required proof |
| --- | --- |
| G1 — static build/unit | Exact-head WSL2 runner TRX JSON, PASS |
| G2 — isolated database journal | Actual `IBKR OFFLINE POSTGRES SMOKE PASS ...` from CLI |
| G3 — real API | `TRADEOPS REAL API INITIAL PASS` and `RESTART PASS` |
| G4 — real Worker | `TRADEOPS REAL WORKER START PASS` |
| G5 — cleanup | Final `TRADEOPS REAL POSTGRES ACCEPTANCE PASS` and exit code 0 |

Treat G2–G5 as NOT RUN until verified output is collected from the
trusted WSL2 host. If a stage fails, use the printed stage code to
debug; do not enable broker access as a workaround.


### Environment diagnostic

The first previous run from the owner Windows Git worktree stopped at
`line 4: set: pipefail` due CRLF normalization. Fixed by a repository
`.gitattributes` forcing LF for WSL `.sh` and `.py` paths.
The next real attempt stopped at `FAIL stage=missing_dotnet`: the
interactive WSL shell did not have `dotnet` on PATH. Neither failure
ever reached the PostgreSQL container. The new SDK container fallback
addresses this without changing the owner's WSL packages.

If the next attempt reports `no_cached_dotnet10_sdk_image` or
`cached_image_not_dotnet10_sdk`, inspect cached images and the detached
worktree; **do not** install tools or modify the running relay to make
the test appear successful. A genuine successful result still requires
the actual G2–G5 markers from the disposable runtime acceptance.


### Windows Git worktree / Linux Git mismatch (2026-10-22)

On the first Docker-mode acceptance attempt, Windows Git created a
`.git` worktree pointer with an absolute drive-letter path such as
`D:/Projects/TradeOps-v2/.git/worktrees/...`. Linux Git running under
WSL2 tried to resolve that path relative to `/mnt/d/Projects/...` and
failed with `fatal: not a git repository`, **before PostgreSQL started**.

The Bash acceptance script therefore never performs `git rev-parse`
inside that Windows-origin worktree. It requires one full 40-character
lowercase commit SHA as its positional argument. The Windows launcher
is responsible for verifying it with `git.exe -C <worktree> rev-parse
HEAD` before invoking WSL. The Bash script refuses missing or
invalid SHA strings before creating any container.

This is a Windows/Linux Git interoperability fix, not PostgreSQL or
IBKR runtime qualification. The real PostgreSQL acceptance must still
execute to completion with its actual DB/API/Worker evidence.


### NuGet restore failure diagnosed (October 22, 2026)

The first real acceptance attempt that reached the SDK restore failed
with `NU1101` for packages including EF Core, Npgsql and Swashbuckle.
The original script had run `dotnet restore` under `--network none`
without a populated NuGet cache inside the SDK image. The test never
started PostgreSQL; this was a harness dependency issue, not an
application or database failure.

The revised script:
- Uses a **dedicated official nuget.org-only** source configuration,
  not private/system feeds.
- Enables outbound network **only for NuGet restore**, and disables it
  again for build and IBKR account-free demos.
- Mounts a **shared ephemeral NuGet global-packages folder** across
  separate restore and build SDK containers so the packages do not
  disappear when the restore container exits.
- Caps restore to 360 s and build to 180 s; emits a sanitized heartbeat
  every ~10 s and reports categorized error codes. SDK Docker
  containers are removed even after a timeout.
- Drops the ephemeral cache during cleanup. The host's existing NuGet
  cache and the independent WSL2 runner are untouched.

This does allow an official public package-download connection during
restore (the image, PostgreSQL and broker transports are still
offline/loopback-only). If the WSL2 host cannot reach the NuGet source,
runtime acceptance remains blocked until an authorized local source or
pre-provisioned offline cache is available. Never claim a PostgreSQL
PASS merely because restore succeeds.


### Root-owned NuGet files and incorrect cleanup (October 22, 2026)

The first nuget.org-enabled restore created hundreds of NuGet files
owned by `root` on the WSL2 host. A disposable .NET SDK Docker
container wrote them into `/tmp/tradeops-real-pg.<random>/nuget-*`
using the container's default root identity. The enclosing Bash
process was a normal WSL user and therefore could not remove the
cache. The prior cleanup erroneously printed `cleanup=PASS`, because
the `rm -rf` failure was not checked.

Fixed:
- Every SDK Docker build/restore/journal/demo invocation now runs
  with explicit `--user "$(id -u):$(id -g)"`; its bind-mounted
  ephemeral NuGet cache and generated files are user-owned.
- Both the exit status of `rm -rf` and the **absence** of the
  temporary scratch directory are checked in the exit trap.
  Any mismatch prints `FAIL stage=temporary_cache_cleanup`,
  marks cleanup as failed, and forbids the final PASS marker.
- The cleanup trap still removes all uniquely named disposable
  containers and checks their absence. A cleanup failure must not
  be reported as successful, even when functional tests pass.
- Do not use broad `sudo rm -rf /tmp/tradeops-*` or prune Docker:
  unrelated WSL services/containers might be affected.

One prior run left the **exact** directory
`/tmp/tradeops-real-pg.KUjt90rm`. Before deleting it manually,
verify that it is not a symlink, that its parent is exactly
`/tmp`, and that the directory itself belongs to the invoking
WSL user. The retained root-owned *contents* may then be removed
in a disposable SDK Docker helper which bind-mounts **only that
directory**, with `--network none`. A permanent root shell or
broad recursive system cleanup is unnecessary. The main runner
never automatically deletes unrelated old run directories.

Old acceptance outputs, including any prior `cleanup=PASS` on a
failed run, do **not** qualify as runtime PostgreSQL acceptance.


### Real PostgreSQL smoke errors are now diagnosable

`TradeOps.IbkrOfflinePostgresSmoke` emits only a controlled failure
`phase`, exception type and (when appropriate) PostgreSQL SQLSTATE.
It never includes raw database connection strings, passwords, raw
broker messages or SQL statements. The one-shot WSL acceptance script
forwards only these sanitized markers when the executable reports an
error, and still deletes the temporary database/container. Examples:

```text
IBKR OFFLINE POSTGRES SMOKE FAIL phase=reconciliation_and_atomic_rollback type=PostgresException sqlstate=23505
TRADEOPS REAL POSTGRES ACCEPTANCE FAIL stage=real_postgresql_journal_smoke
```

No real PostgreSQL runtime PASS can be inferred from these diagnostic
facilities. A complete, verified PASS is still required.

  
### Security audit restore blocker — MessagePack (October 22, 2026)

After enabling a real NuGet restore, the official advisory feed identified
`MessagePack 3.1.8` with `NU1902`; the strict offline IBKR demo
correctly treated this warning as an error. This was a genuine
dependency security finding rather than a PostgreSQL failure. The
vendor advisory GHSA-qhrr-8q5h-9q3h / CVE-2026-92707 identifies a
denial-of-service allocation issue with affected 3.x versions below
3.1.9. The package reference in
`src/TradeOps.Infrastructure/TradeOps.Infrastructure.csproj` is now
pinned to `MessagePack 3.1.12` (released 2026-10-12), above the patched
minimum; no suppression via `NoWarn`, `NU1902`, or disabled NuGet audit.
The ordinary WSL2 unit-test gate also checks the vulnerable version
cannot be reintroduced silently.

Source: https://github.com/MessagePack-CSharp/MessagePack-CSharp/security/advisories/GHSA-qhrr-8q5h-9q3h
Package: https://www.nuget.org/packages/MessagePack/3.1.12

The actual local PostgreSQL/API runtime gates remain **NOT RUN**
until the new qualified HEAD is executed in WSL2.


### Real PostgreSQL / first API success, and shell process lifecycle bug (2026-10-22)

The owner ran the exact qualified `087cb57f...` acceptance head on
the trusted WSL2 host. These **real runtime** gates passed:

- Real NuGet restore and real `dotnet build` from the checked-out source.
- Actual disposable PostgreSQL **17** server version check.
- Real Npgsql journal transaction/concurrency/reconciliation smoke:
  `TRADEOPS REAL POSTGRES JOURNAL PASS`.
- Account-free, zero-broker-request IBKR offline executable:
  `TRADEOPS IBKR ACCOUNT-FREE DEMO PASS`.
- Actual ASP.NET Core HTTP + PostgreSQL end-to-end initial scenario:
  `TRADEOPS REAL API INITIAL PASS`, including mock signal, idempotent
  replay, conflicting signal HTTP 409, real PostgreSQL readiness, and
  mocked reconciliation to `Filled`.

The run **failed after the initial HTTP PASS**, when `stop_api`
successfully removed the disposable API Docker container and performed
an expected `docker inspect` of the now-absent name. The previous code:

```bash
docker inspect "$api_container" >/dev/null 2>&1 && fail "api_container_cleanup"
```

returned exit **1** from the overall `stop_api` function when the
container was correctly absent. Bash `set -e` terminated the script
without reaching API restart, Worker, or SQL count checks. This was a
harness false-negative, **not evidence of an application bug**.

The fix uses an explicit `if docker inspect ...; then fail; fi`
guard and `return 0` for the expected absent-container postcondition.
Two Linux-run Bash regression tests execute the **real function**
under strict `set -e`, covering both absent (success) and surviving
(failure) containers. Any future unexpected shell failure now emits
a sanitized `stage=unhandled_shell_error line=<line> exit=<status>`
without dumping the shell command or secrets.

**Evidence classification:** G2 (real IBKR journal) PASS, G3 initial
API HTTP PASS; G3 restart, G4 Worker, G5 end-to-end PASS remain
**NOT YET PROVEN**. The overall acceptance is FAIL until the full
script reaches its final PASS marker on a new, exact-head, locally
executed run.
