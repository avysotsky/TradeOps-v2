#!/usr/bin/env bash
# TO-V2-14: REAL local .NET + PostgreSQL acceptance. No real broker connectivity.
# One-shot, isolated, ephemeral. Run only from a deliberately detached worktree.
set -Eeuo pipefail
umask 077

ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd -P)"
cd "$ROOT"

fail() {
    local stage="$1"
    printf 'TRADEOPS REAL POSTGRES ACCEPTANCE FAIL stage=%s\n' "$stage" >&2
    # Print only categorized SDK error diagnostics (never raw logs, env,
    # passwords or connection strings). Detailed scratch logs are erased
    # by the cleanup trap to avoid exposing random acceptance credentials.
    if [[ "${scratch:-}" != '' ]]; then
        case "$stage" in
            solution_restore|solution_build)
                local diagnostic="$scratch/${stage#solution_}.log"
                if [[ -f "$diagnostic" ]]; then
                    grep -E '(^|[[:space:]])(error|NU[0-9]{4}|MSB[0-9]{4}|CS[0-9]{4})(:|[[:space:]])' \
                        "$diagnostic" | sed -E 's#https?://[^[:space:]]+#<URL-REDACTED>#g' \
                        | tail -n 12 >&2 || true
                fi
                ;;
        esac
    fi
    exit 1
}

# A worktree created by Windows Git has a .git file containing a
# Windows drive-letter path. Linux git rev-parse cannot resolve it.
# The PowerShell launcher verifies the exact detached HEAD with git.exe
# and passes the verified full commit SHA as the ONLY script argument.
head="${1:-}"
[[ "$head" =~ ^[0-9a-f]{40}$ ]] ||
    fail "missing_or_invalid_verified_head"

for dependency in docker python3 curl flock timeout; do
    command -v "$dependency" >/dev/null 2>&1 || fail "missing_${dependency}"
done
docker info >/dev/null 2>&1 || fail "docker_engine_unavailable"
docker image inspect postgres:17-bookworm >/dev/null 2>&1 ||
    fail "local_postgres_17_image_missing_no_automatic_pull"

# The interactive WSL2 shell may NOT have a dotnet CLI even though the
# autonomous WSL2 test runner has a local .NET 10 SDK *Docker image*.
# Never download/install an SDK or modify an existing WSL2 service.
dotnet_mode=native
dotnet_image=''
if ! command -v dotnet >/dev/null 2>&1 ||
   ! dotnet --list-sdks 2>/dev/null | grep -Eq '^10\.'; then
    dotnet_mode=docker
    for candidate in jobradar-pr-autotest:dotnet10 mcr.microsoft.com/dotnet/sdk:10.0; do
        if docker image inspect "$candidate" >/dev/null 2>&1; then
            dotnet_image="$candidate"
            break
        fi
    done
    [[ -n "$dotnet_image" ]] || fail "no_cached_dotnet10_sdk_image"
    if ! docker run --rm --pull=never --network none --entrypoint dotnet \
        "$dotnet_image" --list-sdks 2>/dev/null | grep -Eq '^10\.'; then
        fail "cached_image_not_dotnet10_sdk"
    fi
fi

# Build and CLI acceptance use the same cached .NET 10 SDK.
# NuGet's expanded packages MUST survive separate disposable SDK containers:
# restore and build run in different processes. Cache is inside a temporary,
# per-run directory, never a shared system SDK cache or an existing WSL service.
# Network access is permitted ONLY during restore, with official nuget.org as
# the sole package source (scripts/ibkr/to-v2-14-nuget.config).
run_dotnet() {
    local network="$1"
    shift
    if [[ "$dotnet_mode" == native ]]; then
        NUGET_PACKAGES="$scratch/nuget-packages" \
            NUGET_HTTP_CACHE_PATH="$scratch/nuget-http" \
            DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
            dotnet "$@"
    else
        docker run --rm --pull=never \
            --network "$network" --cpus=2 --memory=2g --pids-limit=160 \
            --mount "type=bind,source=$ROOT,target=/workspace" \
            --mount "type=bind,source=$scratch/nuget-packages,target=/acceptance-nuget/packages" \
            --mount "type=bind,source=$scratch/nuget-http,target=/acceptance-nuget/http" \
            --workdir /workspace \
            -e DOTNET_CLI_HOME=/tmp \
            -e DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
            -e NUGET_PACKAGES=/acceptance-nuget/packages \
            -e NUGET_HTTP_CACHE_PATH=/acceptance-nuget/http \
            -e TRADEOPS_IBKR_OFFLINE_SMOKE_CONFIRM \
            -e TRADEOPS_IBKR_OFFLINE_SMOKE_PG \
            --entrypoint dotnet \
            "$dotnet_image" "$@"
    fi
}
printf 'TRADEOPS REAL POSTGRES DOTNET MODE=%s\n' "$dotnet_mode"

exec 9>"${TMPDIR:-/tmp}/tradeops-ibkr-real-pg-acceptance.lock"
flock -n 9 || fail "another_real_postgres_acceptance_is_running"

scratch="$(mktemp -d "${TMPDIR:-/tmp}/tradeops-real-pg.XXXXXXXX")"
mkdir -p "$scratch/nuget-packages" "$scratch/nuget-http"
run_suffix="$(python3 -c 'import secrets;print(secrets.token_hex(6))')"
pg_container="tradeops-ibkr-realpg-${run_suffix}"
api_container="tradeops-ibkr-realapi-${run_suffix}"
worker_container="tradeops-ibkr-realworker-${run_suffix}"
journal_db="tradeops_ibkr_offline_smoke_${run_suffix}"
api_db="tradeops_api_smoke_${run_suffix}"
password="$(python3 -c 'import secrets;print(secrets.token_hex(24))')"
signal_key="$(python3 -c 'import secrets;print(secrets.token_hex(24))')"
operator_key="$(python3 -c 'import secrets;print(secrets.token_hex(24))')"
# HEAD was verified on the Windows side; no Linux Git worktree lookup.
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
    for disposable in "$api_container" "$worker_container"; do
        if docker inspect "$disposable" >/dev/null 2>&1; then
            docker rm -f "$disposable" >/dev/null 2>&1 || cleanup_failed=1
        fi
        docker inspect "$disposable" >/dev/null 2>&1 && cleanup_failed=1
    done
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
# The SDK container may have no NuGet packages. Allow access to official
# nuget.org only for restore; builds and all offline broker demos run with
# Docker networking disabled. No actual trading or broker requests.
printf 'TRADEOPS REAL POSTGRES RESTORE START source=nuget.org-only network=restore-only\n'
if ! timeout --signal=TERM --kill-after=10s 360s \
    run_dotnet host restore TradeOps.sln \
    --configfile scripts/ibkr/to-v2-14-nuget.config \
    --verbosity minimal >"$scratch/restore.log" 2>&1; then
    fail "solution_restore"
fi
printf 'TRADEOPS REAL POSTGRES RESTORE PASS\n'
printf 'TRADEOPS REAL POSTGRES BUILD START\n'
if ! timeout --signal=TERM --kill-after=10s 180s \
    run_dotnet none build TradeOps.sln -c Release --no-restore -v:q >"$scratch/build.log" 2>&1; then
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
server_version="$(docker exec "$pg_container" psql -U postgres -d "$journal_db" -At \
    -c 'SHOW server_version_num' | tr -d '[:space:]')"
[[ "$server_version" =~ ^17[0-9]{4}$ ]] || fail "wrong_postgresql_server_version"
printf 'TRADEOPS REAL POSTGRES VERSION PASS major=17\n'

# The real API runs against ANOTHER real disposable database in the same
# ephemeral PostgreSQL cluster, never against the owner's production DB.
docker exec "$pg_container" psql -U postgres -d postgres -v ON_ERROR_STOP=1 \
    -c "CREATE DATABASE \"${api_db}\"" >"$scratch/createdb.log" 2>&1 ||
    fail "isolated_api_database_creation"

journal_connection="Host=127.0.0.1;Port=${pg_port};Database=${journal_db};Username=postgres;Password=${password}"
export TRADEOPS_IBKR_OFFLINE_SMOKE_CONFIRM=DISPOSABLE_LOCAL_POSTGRES_ONLY
export TRADEOPS_IBKR_OFFLINE_SMOKE_PG="$journal_connection"

if ! run_dotnet host "tools/TradeOps.IbkrOfflinePostgresSmoke/bin/Release/net10.0/TradeOps.IbkrOfflinePostgresSmoke.dll" >"$scratch/journal.log" 2>&1; then
    fail "real_postgresql_journal_smoke"
fi
grep -Eq '^IBKR OFFLINE POSTGRES SMOKE PASS cases=[0-9]+ broker_requests=0 cleanup=true$'     "$scratch/journal.log" || fail "journal_smoke_success_marker_missing"
printf 'TRADEOPS REAL POSTGRES JOURNAL PASS\n'
unset TRADEOPS_IBKR_OFFLINE_SMOKE_PG

# Account-free IBKR read-path synthetic transport demo: executable, not just
# compiled. It must explicitly state zero external/broker requests.
if ! run_dotnet none "tools/TradeOps.IbkrOfflineDemo/bin/Release/net10.0/TradeOps.IbkrOfflineDemo.dll" >"$scratch/ibkr-demo.log" 2>&1; then
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

api_is_running() {
    if [[ "$dotnet_mode" == native ]]; then
        [[ -n "$api_pid" ]] && kill -0 "$api_pid" 2>/dev/null
    else
        [[ "$(docker inspect -f '{{.State.Running}}' "$api_container" 2>/dev/null || true)" == true ]]
    fi
}

stop_api() {
    if [[ "$dotnet_mode" == native ]]; then
        stop_process "$api_pid"
        api_pid=''
    else
        docker rm -f "$api_container" >/dev/null 2>&1 ||
            fail "api_container_stop"
        docker inspect "$api_container" >/dev/null 2>&1 &&
            fail "api_container_cleanup"
    fi
}

start_api() {
    local label="$1"
    if [[ "$dotnet_mode" == native ]]; then
        (
            cd "$ROOT/src/TradeOps.Api"
            exec dotnet bin/Release/net10.0/TradeOps.Api.dll
        ) >"$scratch/${label}.log" 2>&1 &
        api_pid=$!
    else
        docker run --detach --rm --pull=never \
            --name "$api_container" --network host \
            --cpus=2 --memory=1g --pids-limit=160 \
            --mount "type=bind,source=$ROOT,target=/workspace,readonly" \
            --workdir /workspace/src/TradeOps.Api \
            -e ConnectionStrings__TradeOpsDb \
            -e Exchange__Provider \
            -e Telegram__Enabled \
            -e Integrations__TradingView__Enabled \
            -e SignalIngress__Authentication__Enabled \
            -e SignalIngress__Authentication__ApiKey \
            -e OperatorApi__Authentication__Enabled \
            -e OperatorApi__Authentication__ApiKey \
            -e ASPNETCORE_ENVIRONMENT \
            -e DOTNET_ENVIRONMENT \
            -e ASPNETCORE_URLS \
            --entrypoint dotnet "$dotnet_image" \
            bin/Release/net10.0/TradeOps.Api.dll \
            >"$scratch/${label}.container-id" 2>"$scratch/${label}.docker.log" ||
            fail "${label}_container_start"
    fi

    local http_status
    for _ in $(seq 1 75); do
        api_is_running || fail "${label}_exited_before_ready"
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
stop_api
printf 'TRADEOPS REAL API INITIAL PASS\n'

# Restart the REAL ASP.NET Core process on the SAME PostgreSQL database.
start_api "api-restarted"
TRADEOPS_ACCEPTANCE_PHASE=restart python3 \
    scripts/ibkr/to-v2-14-api-http-acceptance.py ||
    fail "api_real_http_postgres_restart"
stop_api

# Verify a second real executable (Worker) starts with the real database,
# using Mock venue and no outbound brokerage service.
if [[ "$dotnet_mode" == native ]]; then
    (
        cd "$ROOT/src/TradeOps.Worker"
        exec dotnet bin/Release/net10.0/TradeOps.Worker.dll
    ) >"$scratch/worker.log" 2>&1 &
    worker_pid=$!
else
    docker run --detach --rm --pull=never \
        --name "$worker_container" --network host \
        --cpus=2 --memory=1g --pids-limit=160 \
        --mount "type=bind,source=$ROOT,target=/workspace,readonly" \
        --workdir /workspace/src/TradeOps.Worker \
        -e ConnectionStrings__TradeOpsDb \
        -e Exchange__Provider \
        -e Telegram__Enabled \
        -e ASPNETCORE_ENVIRONMENT \
        -e DOTNET_ENVIRONMENT \
        --entrypoint dotnet "$dotnet_image" \
        bin/Release/net10.0/TradeOps.Worker.dll \
        >"$scratch/worker.container-id" 2>"$scratch/worker.docker.log" ||
        fail "worker_container_start"
fi

worker_is_running() {
    if [[ "$dotnet_mode" == native ]]; then
        [[ -n "$worker_pid" ]] && kill -0 "$worker_pid" 2>/dev/null
    else
        [[ "$(docker inspect -f '{{.State.Running}}' "$worker_container" 2>/dev/null || true)" == true ]]
    fi
}

worker_ready=0
for _ in $(seq 1 20); do
    worker_is_running || fail "worker_process_crashed"
    if [[ "$dotnet_mode" == native ]]; then
        grep -q 'Application started' "$scratch/worker.log" && worker_ready=1
    else
        docker logs "$worker_container" 2>&1 |
            grep -q 'Application started' && worker_ready=1
    fi
    if [[ "$worker_ready" == 1 ]]; then
        break
    fi
    sleep 1
done
[[ "$worker_ready" == 1 ]] || fail "worker_not_started"
sleep 3
worker_is_running || fail "worker_exited_after_start"
if [[ "$dotnet_mode" == native ]]; then
    stop_process "$worker_pid"
    worker_pid=''
else
    docker rm -f "$worker_container" >/dev/null 2>&1 ||
        fail "worker_container_stop"
fi
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
