#!/usr/bin/env bash
# TO-V2-30: never migrate persistent PostgreSQL.
set -Eeuo pipefail
umask 077
fail() { printf 'TRADEOPS_PG17_SCHEMA_REHEARSAL_FAIL code=%s\n' "$1" >&2; exit 1; }
[[ $# -eq 2 && -f "$1" && ! -L "$1" && -f "$2" && ! -L "$2" ]] || fail PINNED_FILES_MISSING
schema_sql="$1"
fixture_sql="$2"
source='tradeops-deribit-evidence-pg17'
volume='tradeops-deribit-evidence-pg17-data'
backup_dir="$HOME/.local/share/tradeops/deribit-evidence-pg17/backups"
command -v docker >/dev/null 2>&1 || fail DOCKER_MISSING
docker info >/dev/null 2>&1 || fail DOCKER_UNAVAILABLE
[[ "$(docker inspect -f '{{.State.Health.Status}}' "$source" 2>/dev/null || true)" == healthy ]] || fail SOURCE_UNHEALTHY
[[ "$(docker inspect -f '{{ index .Config.Labels "com.tradeops.component" }}' "$source")" == deribit-readonly-evidence ]] || fail SOURCE_LABEL
[[ "$(docker inspect -f '{{range .Mounts}}{{if eq .Destination "/var/lib/postgresql/data"}}{{.Name}}{{end}}{{end}}' "$source")" == "$volume" ]] || fail SOURCE_VOLUME
identity="$(docker exec -u postgres "$source" psql -X -q -A -t -v ON_ERROR_STOP=1 -U postgres -d tradeops_evidence -c "SELECT current_database() || '|' || (current_setting('server_version_num')::int / 10000)::text || '|' || (SELECT count(*) FROM pg_namespace WHERE nspname='broker_evidence')::text")" || fail SOURCE_IDENTITY
[[ "$identity" == 'tradeops_evidence|17|0' ]] || fail SOURCE_HAS_SCHEMA

[[ -d "$backup_dir" && ! -L "$backup_dir" ]] || fail BACKUP_DIR_MISSING
[[ "$(stat -c %u "$backup_dir")" == "$(id -u)" ]] || fail BACKUP_DIR_OWNER
backup_count=0
while IFS= read -r -d '' check; do
  [[ -f "$check" && ! -L "$check" ]] || fail BACKUP_CHECKSUM_UNSAFE
  (cd -- "$backup_dir" && sha256sum --check --status -- "$(basename "$check")") || fail BACKUP_CHECKSUM_FAILED
  backup_count=$((backup_count+1))
done < <(find "$backup_dir" -maxdepth 1 -type f -name 'pre-migration-*.dump.sha256' -print0)
(( backup_count > 0 )) || fail NO_VERIFIED_BACKUP

nonce="$(od -An -N6 -tx1 /dev/urandom | tr -d ' \n')"
[[ "$nonce" =~ ^[0-9a-f]{12}$ ]] || fail NONCE_FAILED
temp="tradeops-schema-rehearsal-$nonce"
docker inspect "$temp" >/dev/null 2>&1 && fail TEMP_EXISTS
created=0
cleanup() {
  if [[ "$created" == 1 ]]; then
    label="$(docker inspect -f '{{ index .Config.Labels "com.tradeops.rehearsal_nonce" }}' "$temp" 2>/dev/null || true)"
    if [[ "$label" == "$nonce" ]]; then
      docker rm -f "$temp" >/dev/null 2>&1 || true
    fi
  fi
}
trap cleanup EXIT
docker run --detach --rm --name "$temp" \
  --network none --tmpfs /var/lib/postgresql/data:rw,size=512m \
  --env POSTGRES_HOST_AUTH_METHOD=trust \
  --env POSTGRES_DB=tradeops_schema_rehearsal \
  --label "com.tradeops.rehearsal_nonce=$nonce" \
  postgres:17-bookworm >/dev/null || fail TEMP_START
created=1
ready=0
for _ in $(seq 1 45); do
  if docker exec -u postgres "$temp" pg_isready -q -U postgres -d tradeops_schema_rehearsal >/dev/null 2>&1; then
    ready=1; break
  fi
  sleep 2
done
[[ "$ready" == 1 ]] || fail TEMP_UNHEALTHY
docker exec -i -u postgres "$temp" psql -X -q -v ON_ERROR_STOP=1 -U postgres -d tradeops_schema_rehearsal < "$schema_sql" >/dev/null || fail SCHEMA_REHEARSAL
docker exec -i -u postgres "$temp" psql -X -q -v ON_ERROR_STOP=1 -U postgres -d tradeops_schema_rehearsal < "$fixture_sql" >/dev/null || fail INTEGRITY_FIXTURES
tables="$(docker exec -u postgres "$temp" psql -X -q -A -t -v ON_ERROR_STOP=1 -U postgres -d tradeops_schema_rehearsal -c "SELECT count(*) FROM information_schema.tables WHERE table_schema='broker_evidence' AND table_type='BASE TABLE'")" || fail TABLE_COUNT
[[ "$tables" == 5 ]] || fail UNEXPECTED_TABLE_COUNT
after="$(docker exec -u postgres "$source" psql -X -q -A -t -v ON_ERROR_STOP=1 -U postgres -d tradeops_evidence -c "SELECT count(*) FROM pg_namespace WHERE nspname='broker_evidence'")" || fail SOURCE_FINAL_CHECK
[[ "$after" == 0 ]] || fail SOURCE_CHANGED
[[ "$(docker inspect -f '{{ index .Config.Labels "com.tradeops.rehearsal_nonce" }}' "$temp")" == "$nonce" ]] || fail TEMP_MISMATCH
docker rm -f "$temp" >/dev/null 2>&1 || fail TEMP_REMOVE
created=0
printf 'TRADEOPS_PG17_SCHEMA_REHEARSAL_PASS source=tradeops_evidence major=17 persistent_schema=absent backup_checksum=PASS ephemeral_tables=5 scoped_unique=PASS native_units=PASS persistent_writes=0 broker_calls=0\n'
