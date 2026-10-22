# TO-V2-14 — Real PostgreSQL 17 + real TradeOps API/Worker acceptance

**Purpose:** close the gap between autonomous WSL2 build/unit qualification
and *actual* PostgreSQL/runtime verification. This is not just a TestServer
or EF Core InMemory test: it starts PostgreSQL 17, real .NET 10 console
executables, the real ASP.NET Core API, and the real Worker process.

**Security:** Mock exchange only; IBKR read-only demo uses synthetic
transport; **no real IBKR account, TWS/Gateway, broker authentication,
order placement or cancellation**. No production PostgreSQL.

## Requirements

- Trusted **user-owned WSL2 Ubuntu** shell with .NET 10 SDK, Python 3,
  curl, flock, Git, Docker Engine and a locally cached
  `postgres:17-bookworm` Docker image. It deliberately will not pull
  a Docker image or install software.
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
bash scripts/ibkr/to-v2-14-run-real-postgres-acceptance.sh
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
