#!/usr/bin/env bash
# TO-V2-14: REAL local .NET + PostgreSQL acceptance. No real broker connectivity.
# One-shot, isolated, ephemeral. Run only from a deliberately detached worktree.
set -Eeuo pipefail
umask 077

ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd -P)"
cd "$ROOT"

fail() {
    printf 'TRADEOPS REAL POSTGRES ACCEPTANCE FAIL stage=%s\n' "$1" >&2
    exit 1
}

for dependency in docker dotnet python3 curl flock git; do
    command -v "$dependency" >/dev/null 2>&1 || fail "missing_${dependency}"
done
dotnet --list-sdks | grep -Eq '^10\.' || fail "dotnet_10_sdk_missing"
docker info >/dev/null 2>&1 || fail "docker_engine_unavailable"
docker image inspect postgres:17-bookworm >/dev/null 2>&1 ||
    fail "local_postgres_17_image_missing_no_automatic_pull"

exec 9>"${TMPDIR:-/tmp}/tradeops-ibkr-real-pg-acceptance.lock"
flock -n 9 || fail "another_real_postgres_acceptance_is_running"

scratch="$(mktemp -d "${TMPDIR:-/tmp}/tradeops-real-pg.XXXXXXXX")"
run_suffix="$(python3 -c 'import secrets;print(secrets.token_hex(6))')"
pg_container="tradeops-ibkr-realpg-${run_suffix}"
journal_db="tradeops_ibkr_offline_smoke_${run_suffix}"
api_db="tradeops_api_smoke_${run_suffix}"
password="$(python3 -c 'import secrets;print(secrets.token_hex(24))')"
signal_key="$(python3 -c 'import secrets;print(secrets.token_hex(24))')"
operator_key="$(python3 -c 'import secrets;print(secrets.token_hex(24))')"
head="$(git rev-parse HEAD)"
api_pid=''
worker_pid=''
pg_started=0
completed=0

stop_process() {
    local pid="${1:-}"
    if [[ -n "$pid" ]] && kill -0 "$pid" 2>/dev/null; then
        kill -TERM "$pid" 2>/dev/null || true
        for _ in 1 2 3 4 5 6 7 8 9 10; do
            kill -0 "$pid" 2>/dev/null || break
            sleep 0.2
        done
        if kill -0 "$pid" 2>/dev/null; then
            kill -KILL "$pid" 2>/dev/null || true
        fi
        wait "$pid" 2>/dev/null || true
    fi
}

cleanup() {
    local code=$?
    trap - EXIT
    set +e
    stop_process "$api_pid"
    stop_process "$worker_pid"
    local cleanup_failed=0
    if [[ "$pg_started" == 1 ]]; then
        if docker inspect "$pg_container" >/dev/null 2>&1; then
            docker rm -f "$pg_container" >/dev/null 2>&1 || cleanup_failed=1
        fi
        docker inspect "$pg_container" >/dev/null 2>&1 && cleanup_failed=1
    fi
    rm -rf -- "$scratch"
    unset password signal_key operator_key POSTGRES_PASSWORD
    unset ConnectionStrings__TradeOpsDb TRADEOPS_IBKR_OFFLINE_SMOKE_PG
    if [[ "$code" -eq 0 && "$cleanup_failed" -eq 0 && "$completed" == 1 ]]; then
        printf 'TRADEOPS REAL POSTGRES ACCEPTANCE PASS sha=%s pg=17 journal=PASS api=PASS restart=PASS worker=PASS cleanup=PASS broker_requests=0\n' "$head"
        exit 0
    fi
    printf 'TRADEOPS REAL POSTGRES ACCEPTANCE FAIL sha=%s cleanup=%s\n' "$head"         "$([[ "$cleanup_failed" -eq 0 ]] && echo PASS || echo FAIL)" >&2
    exit 1
}
trap cleanup EXIT

printf 'TRADEOPS REAL POSTGRES ACCEPTANCE start sha=%s mode=isolated_mock_only\n' "$head"

# New detached worktrees require their own obj/project.assets.json.
# Do NOT invoke network-accessing broker adapters or existing WSL relay services.
if ! dotnet restore TradeOps.sln --ignore-failed-sources --verbosity quiet >"$scratch/restore.log" 2>&1; then
    fail "solution_restore"
fi
if ! dotnet build TradeOps.sln -c Release --no-restore -v:q >"$scratch/build.log" 2>&1; then
    fail "solution_build"
fi
printf 'TRADEOPS REAL POSTGRES BUILD PASS\n'

# Use ONLY an already cached, immutable-lab image. No docker pull, volume
# mounts, existing DB, networked PostgreSQL, broker credentials or host services.
export POSTGRES_PASSWORD="$password"
if ! docker run --rm --detach --pull=never \
    --name "$pg_container" \
    --label "tradeops.acceptance=ibkr-offline" \
    --cpus=2 --memory=1g \
    -e POSTGRES_PASSWORD \
    -e POSTGRES_DB="$journal_db" \
    -p 127.0.0.1::5432 \
    postgres:17-bookworm >"$scratch/container-id" 2>"$scratch/docker.log"; then
    fail "ephemeral_postgres_start"
fi
pg_started=1
pg_port="$(docker port "$pg_container" 5432/tcp |
    sed -n 's/^127\.0\.0\.1:\([0-9][0-9]*\)$/\1/p' | head -n 1)"
[[ "$pg_port" =~ ^[0-9]+$ ]] || fail "loopback_port_discovery"

ready=0
for _ in $(seq 1 75); do
    if docker exec "$pg_container" pg_isready -q -U postgres -d "$journal_db" >/dev/null 2>&1; then
        ready=1
        break
    fi
    sleep 1
done
[[ "$ready" == 1 ]] || fail "postgres_ready"

# The real API runs against ANOTHER real disposable database in the same
# ephemeral PostgreSQL cluster, never against the owner's production DB.
docker exec "$pg_container" psql -U postgres -d postgres -v ON_ERROR_STOP=1 \
    -c "CREATE DATABASE \"${api_db}\"" >"$scratch/createdb.log" 2>&1 ||
    fail "isolated_api_database_creation"

journal_connection="Host=127.0.0.1;Port=${pg_port};Database=${journal_db};Username=postgres;Password=${password}"
export TRADEOPS_IBKR_OFFLINE_SMOKE_CONFIRM=DISPOSABLE_LOCAL_POSTGRES_ONLY
export TRADEOPS_IBKR_OFFLINE_SMOKE_PG="$journal_connection"

if ! dotnet "tools/TradeOps.IbkrOfflinePostgresSmoke/bin/Release/net10.0/TradeOps.IbkrOfflinePostgresSmoke.dll" >"$scratch/journal.log" 2>&1; then
    fail "real_postgresql_journal_smoke"
fi
grep -Eq '^IBKR OFFLINE POSTGRES SMOKE PASS cases=[0-9]+ broker_requests=0 cleanup=true$'     "$scratch/journal.log" || fail "journal_smoke_success_marker_missing"
printf 'TRADEOPS REAL POSTGRES JOURNAL PASS\n'
unset TRADEOPS_IBKR_OFFLINE_SMOKE_PG

# Account-free IBKR read-path synthetic transport demo: executable, not just
# compiled. It must explicitly state zero external/broker requests.
if ! dotnet "tools/TradeOps.IbkrOfflineDemo/bin/Release/net10.0/TradeOps.IbkrOfflineDemo.dll" >"$scratch/ibkr-demo.log" 2>&1; then
    fail "ibkr_read_path_offline_demo"
fi
grep -q 'IBKR ACCOUNT-FREE OFFLINE DEMO PASS' "$scratch/ibkr-demo.log" ||
    fail "ibkr_demo_success_marker_missing"
printf 'TRADEOPS IBKR ACCOUNT-FREE DEMO PASS\n'

api_port="$(python3 -c 'import socket;s=socket.socket();s.bind(("127.0.0.1",0));print(s.getsockname()[1]);s.close()')"
export ConnectionStrings__TradeOpsDb="Host=127.0.0.1;Port=${pg_port};Database=${api_db};Username=postgres;Password=${password}"
export Exchange__Provider=Mock
export Telegram__Enabled=false
export Integrations__TradingView__Enabled=false
export SignalIngress__Authentication__Enabled=true
export SignalIngress__Authentication__ApiKey="$signal_key"
export OperatorApi__Authentication__Enabled=true
export OperatorApi__Authentication__ApiKey="$operator_key"
export ASPNETCORE_ENVIRONMENT=Production
export DOTNET_ENVIRONMENT=Production
export ASPNETCORE_URLS="http://127.0.0.1:${api_port}"

export TRADEOPS_ACCEPTANCE_API_URL="$ASPNETCORE_URLS"
export TRADEOPS_ACCEPTANCE_SIGNAL_KEY="$signal_key"
export TRADEOPS_ACCEPTANCE_OPERATOR_KEY="$operator_key"
export TRADEOPS_ACCEPTANCE_STATE="$scratch/state.json"

start_api() {
    local label="$1"
    (
        cd "$ROOT/src/TradeOps.Api"
        exec dotnet bin/Release/net10.0/TradeOps.Api.dll
    ) >"$scratch/${label}.log" 2>&1 &
    api_pid=$!
    local http_status
    for _ in $(seq 1 75); do
        kill -0 "$api_pid" 2>/dev/null || fail "${label}_exited_before_ready"
        http_status="$(curl --noproxy '*' --silent --output /dev/null \
            --write-out '%{http_code}' --max-time 2 \
            "http://127.0.0.1:${api_port}/health/ready" || true)"
        if [[ "$http_status" == 200 ]]; then
            return
        fi
        sleep 1
    done
    fail "${label}_health_ready_timeout"
}

start_api "api-first"
TRADEOPS_ACCEPTANCE_PHASE=initial python3 \
    scripts/ibkr/to-v2-14-api-http-acceptance.py ||
    fail "api_real_http_and_postgresql_initial"
stop_process "$api_pid"
api_pid=''
printf 'TRADEOPS REAL API INITIAL PASS\n'

# Restart the REAL ASP.NET Core process on the SAME PostgreSQL database.
start_api "api-restarted"
TRADEOPS_ACCEPTANCE_PHASE=restart python3 \
    scripts/ibkr/to-v2-14-api-http-acceptance.py ||
    fail "api_real_http_postgres_restart"
stop_process "$api_pid"
api_pid=''

# Verify a second real executable (Worker) starts with the real database,
# using Mock venue and no outbound brokerage service.
(
    cd "$ROOT/src/TradeOps.Worker"
    exec dotnet bin/Release/net10.0/TradeOps.Worker.dll
) >"$scratch/worker.log" 2>&1 &
worker_pid=$!
worker_ready=0
for _ in $(seq 1 20); do
    kill -0 "$worker_pid" 2>/dev/null || fail "worker_process_crashed"
    if grep -q 'Application started' "$scratch/worker.log"; then
        worker_ready=1
        break
    fi
    sleep 1
done
[[ "$worker_ready" == 1 ]] || fail "worker_not_started"
sleep 3
kill -0 "$worker_pid" 2>/dev/null || fail "worker_exited_after_start"
stop_process "$worker_pid"
worker_pid=''
printf 'TRADEOPS REAL WORKER START PASS\n'

# Independently verify actual EF Core migrations and persistent database rows.
count="$(docker exec "$pg_container" psql -U postgres -d "$api_db" -At \
    -c 'SELECT count(*) FROM "Orders"' | tr -d '[:space:]')"
[[ "$count" == 1 ]] || fail "api_sql_order_idempotency"
events="$(docker exec "$pg_container" psql -U postgres -d "$api_db" -At \
    -c 'SELECT count(*) FROM "OrderLifecycleEvents"' | tr -d '[:space:]')"
[[ "$events" =~ ^[0-9]+$ && "$events" -ge 2 ]] ||
    fail "api_sql_lifecycle_events"
printf 'TRADEOPS REAL SQL CHECK PASS orders=1 lifecycle_events_present=true\n'

completed=1
