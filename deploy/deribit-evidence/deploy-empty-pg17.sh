#!/usr/bin/env bash
# TO-V2-28: owner-approved ONE-TIME creation of a dedicated EMPTY PostgreSQL 17.
# The script intentionally cannot migrate broker_evidence or import Deribit.
set -Eeuo pipefail

fail() {
  printf 'TRADEOPS_PG17_EMPTY_DEPLOY_FAIL code=%s\n' "$1" >&2
  exit 1
}

[[ $# -eq 1 && -f "$1" && ! -L "$1" ]] || fail INVALID_PINNED_COMPOSE_FILE
compose_file="$1"
project='tradeops-deribit-evidence'
container='tradeops-deribit-evidence-pg17'
volume='tradeops-deribit-evidence-pg17-data'
port='54329'
database='tradeops_evidence'
secret_dir="$HOME/.local/share/tradeops/deribit-evidence-pg17"
secret_file="$secret_dir/postgres-bootstrap-password"

command -v docker >/dev/null 2>&1 || fail DOCKER_UNAVAILABLE
docker info >/dev/null 2>&1 || fail DOCKER_ENGINE_UNAVAILABLE
docker compose version >/dev/null 2>&1 || fail DOCKER_COMPOSE_UNAVAILABLE

# Fail closed rather than adopt an existing container, volume or network.
docker container inspect "$container" >/dev/null 2>&1 && fail CONTAINER_ALREADY_EXISTS
docker volume inspect "$volume" >/dev/null 2>&1 && fail VOLUME_ALREADY_EXISTS
docker network inspect "${project}_default" >/dev/null 2>&1 && fail NETWORK_ALREADY_EXISTS

if command -v ss >/dev/null 2>&1; then
  if [[ -n "$(ss -H -ltn "sport = :$port")" ]]; then
    fail LOCAL_PORT_ALREADY_BOUND
  fi
fi
# Do not inspect other containers' environment variables, password files or DBs.

umask 077
[[ ! -L "$secret_dir" ]] || fail SECRET_DIR_SYMLINK
mkdir -p -- "$secret_dir"
[[ -d "$secret_dir" && ! -L "$secret_dir" ]] || fail SECRET_DIR_UNSAFE
[[ "$(stat -c '%u' "$secret_dir")" == "$(id -u)" ]] || fail SECRET_DIR_WRONG_OWNER
chmod 700 -- "$secret_dir"

if [[ -e "$secret_file" || -L "$secret_file" ]]; then
  [[ -f "$secret_file" && ! -L "$secret_file" ]] || fail SECRET_PATH_UNSAFE
  [[ "$(stat -c '%u' "$secret_file")" == "$(id -u)" ]] || fail SECRET_WRONG_OWNER
  [[ "$(stat -c '%a' "$secret_file")" == "600" ]] || fail SECRET_PERMISSIONS
  [[ "$(wc -c < "$secret_file")" -ge 64 ]] || fail SECRET_TOO_SHORT
else
  tmp_secret="$secret_dir/.bootstrap.$$.tmp"
  [[ ! -e "$tmp_secret" && ! -L "$tmp_secret" ]] || fail SECRET_TEMP_EXISTS
  trap 'rm -f -- "${tmp_secret:-}"' EXIT
  # CSPRNG material generated only inside owner-controlled WSL2.
  od -An -N32 -tx1 /dev/urandom | tr -d ' \n' > "$tmp_secret"
  printf '\n' >> "$tmp_secret"
  chmod 600 -- "$tmp_secret"
  [[ "$(wc -c < "$tmp_secret")" -eq 65 ]] || fail RANDOM_SECRET_FAILED
  mv -n -- "$tmp_secret" "$secret_file"
  [[ -f "$secret_file" && ! -L "$secret_file" ]] || fail SECRET_CREATE_FAILED
  trap - EXIT
fi

export TRADEOPS_EVIDENCE_PG_BOOTSTRAP_PASSWORD_FILE="$secret_file"
# Never print the expanded Docker Compose configuration (it can expose paths).
docker compose -p "$project" -f "$compose_file" config --quiet ||
  fail COMPOSE_VALIDATION_FAILED

# Only the isolated postgres service; no root TradeOps Compose and no workers.
docker compose -p "$project" -f "$compose_file" up -d --no-deps postgres ||
  fail POSTGRES_CONTAINER_START_FAILED

health=''
for _ in $(seq 1 60); do
  health="$(docker inspect -f '{{.State.Health.Status}}' "$container" 2>/dev/null || true)"
  [[ "$health" == healthy ]] && break
  [[ "$health" == unhealthy ]] && fail POSTGRES_UNHEALTHY
  sleep 2
done
[[ "$health" == healthy ]] || fail POSTGRES_HEALTH_TIMEOUT

mounted="$(docker inspect -f '{{range .Mounts}}{{if eq .Destination "/var/lib/postgresql/data"}}{{.Name}}{{end}}{{end}}' "$container")"
[[ "$mounted" == "$volume" ]] || fail PERSISTENT_VOLUME_MISMATCH

# Local socket only; no exposure of password, connection string or records.
verification="$(docker exec -u postgres "$container" psql -X -q -A -t \
  -U postgres -d "$database" -c \
  "SELECT current_database() || '|' || (current_setting('server_version_num')::int / 10000)::text || '|' || (SELECT count(*)::text FROM pg_namespace WHERE nspname='broker_evidence')")" ||
  fail POSTGRES_IDENTITY_QUERY_FAILED

[[ "$verification" == 'tradeops_evidence|17|0' ]] ||
  fail POSTGRES_IDENTITY_OR_UNEXPECTED_SCHEMA

printf 'TRADEOPS_PG17_EMPTY_DEPLOY_PASS database=tradeops_evidence major=17 schema_present=0 persistent_volume_verified=yes status=healthy broker_calls=0 migration_calls=0\n'
