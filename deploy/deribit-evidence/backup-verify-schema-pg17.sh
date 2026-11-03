#!/usr/bin/env bash
# TO-V2-32: BACKUP ONLY from persistent PostgreSQL 17 + restore on isolated tmpfs.
# Owner run. No DB schema migration, broker request, role change or trade import.
set -Eeuo pipefail
umask 077

fail() { printf 'TRADEOPS_PG17_SCHEMA_BACKUP_FAIL code=%s\n' "$1" >&2; exit 1; }
[[ $# -eq 0 ]] || fail UNEXPECTED_ARGUMENTS

source='tradeops-deribit-evidence-pg17'
volume='tradeops-deribit-evidence-pg17-data'
database='tradeops_evidence'
backup_dir="$HOME/.local/share/tradeops/deribit-evidence-pg17/backups"
restore_database='tradeops_evidence_recovery_check'

command -v docker >/dev/null 2>&1 || fail DOCKER_UNAVAILABLE
docker info >/dev/null 2>&1 || fail DOCKER_ENGINE_UNAVAILABLE
[[ "$(docker inspect -f '{{.State.Health.Status}}' "$source" 2>/dev/null || true)" == healthy ]] || fail SOURCE_NOT_HEALTHY
[[ "$(docker inspect -f '{{ index .Config.Labels "com.tradeops.project" }}' "$source")" == TradeOps-v2 ]] || fail SOURCE_PROJECT_MISMATCH
[[ "$(docker inspect -f '{{ index .Config.Labels "com.tradeops.component" }}' "$source")" == deribit-readonly-evidence ]] || fail SOURCE_COMPONENT_MISMATCH
[[ "$(docker inspect -f '{{range .Mounts}}{{if eq .Destination "/var/lib/postgresql/data"}}{{.Name}}{{end}}{{end}}' "$source")" == "$volume" ]] || fail SOURCE_VOLUME_MISMATCH

# Single aggregate query; never output real trade records or credentials.
source_state="$(docker exec -u postgres "$source" psql -X -q -A -t -v ON_ERROR_STOP=1 -U postgres -d "$database" -c "
SELECT current_database() || '|' ||
 (current_setting('server_version_num')::int / 10000)::text || '|' ||
 (SELECT count(*) FROM information_schema.tables WHERE table_schema='broker_evidence' AND table_type='BASE TABLE')::text || '|' ||
 (SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace JOIN pg_roles r ON r.oid=c.relowner
    WHERE n.nspname='broker_evidence' AND c.relkind='r' AND r.rolname='tradeops_evidence_owner')::text || '|' ||
 (SELECT count(*) FROM broker_evidence.account_bindings)::text || '|' ||
 (SELECT count(*) FROM broker_evidence.position_snapshots)::text || '|' ||
 (SELECT count(*) FROM broker_evidence.position_observations)::text || '|' ||
 (SELECT count(*) FROM broker_evidence.execution_observations)::text || '|' ||
 (SELECT count(*) FROM broker_evidence.trade_query_runs)::text;")" || fail SOURCE_QUERY_FAILED
[[ "$source_state" == 'tradeops_evidence|17|5|5|0|0|0|0|0' ]] || fail SOURCE_SCHEMA_OR_DATA_UNEXPECTED

parent="$HOME/.local/share/tradeops/deribit-evidence-pg17"
[[ -d "$parent" && ! -L "$parent" ]] || fail BACKUP_PARENT_UNSAFE
[[ "$(stat -c %u "$parent")" == "$(id -u)" ]] || fail BACKUP_PARENT_NOT_OWNED
[[ ! -L "$backup_dir" ]] || fail BACKUP_DIR_SYMLINK
mkdir -p -- "$backup_dir"
[[ "$(stat -c %u "$backup_dir")" == "$(id -u)" ]] || fail BACKUP_DIR_NOT_OWNED
chmod 700 -- "$backup_dir"

backup_file="$(mktemp "$backup_dir/post-schema-$(date -u +%Y%m%dT%H%M%SZ)-XXXXXX.dump")" ||
  fail BACKUP_FILE_CREATION_FAILED
nonce=''
temp=''
created=0
finished=0
cleanup() {
  if [[ "$created" == 1 ]]; then
    actual="$(docker inspect -f '{{ index .Config.Labels "com.tradeops.schema_backup_nonce" }}' "$temp" 2>/dev/null || true)"
    if [[ "$actual" == "$nonce" ]]; then
      docker rm -f "$temp" >/dev/null 2>&1 || true
    fi
  fi
  if [[ "$finished" != 1 ]]; then
    rm -f -- "$backup_file" "$backup_file.sha256"
  fi
}
trap cleanup EXIT

# Consistent MVCC snapshot; no changes to persistent instance.
docker exec -u postgres "$source" pg_dump --format=custom --no-owner --no-acl \
  -U postgres -d "$database" > "$backup_file" || fail PG_DUMP_FAILED
[[ -s "$backup_file" ]] || fail ARCHIVE_EMPTY
chmod 600 -- "$backup_file"
docker exec -i "$source" pg_restore --list < "$backup_file" >/dev/null ||
  fail ARCHIVE_LIST_FAILED

nonce="$(od -An -N6 -tx1 /dev/urandom | tr -d ' \n')"
[[ "$nonce" =~ ^[0-9a-f]{12}$ ]] || fail NONCE_FAILED
temp="tradeops-schema-backup-restore-$nonce"
docker inspect "$temp" >/dev/null 2>&1 && fail RESTORE_CONTAINER_COLLISION
docker run --detach --rm --name "$temp" \
  --network none --tmpfs /var/lib/postgresql/data:rw,size=512m \
  --env POSTGRES_HOST_AUTH_METHOD=trust \
  --env POSTGRES_DB="$restore_database" \
  --label "com.tradeops.schema_backup_nonce=$nonce" \
  postgres:17-bookworm >/dev/null || fail RESTORE_CONTAINER_START_FAILED
created=1

ready=0
for _ in $(seq 1 45); do
  if docker exec -u postgres "$temp" pg_isready -q -U postgres -d "$restore_database" >/dev/null 2>&1; then
    ready=1
    break
  fi
  sleep 2
done
[[ "$ready" == 1 ]] || fail RESTORE_READY_TIMEOUT

docker exec -i -u postgres "$temp" pg_restore --exit-on-error --no-owner --no-acl \
  -U postgres -d "$restore_database" < "$backup_file" ||
  fail REAL_PG_RESTORE_FAILED

restore_state="$(docker exec -u postgres "$temp" psql -X -q -A -t -v ON_ERROR_STOP=1 -U postgres -d "$restore_database" -c "
SELECT current_database() || '|' ||
 (current_setting('server_version_num')::int / 10000)::text || '|' ||
 (SELECT count(*) FROM information_schema.tables WHERE table_schema='broker_evidence' AND table_type='BASE TABLE')::text || '|' ||
 (SELECT count(*) FROM broker_evidence.account_bindings)::text || '|' ||
 (SELECT count(*) FROM broker_evidence.position_snapshots)::text || '|' ||
 (SELECT count(*) FROM broker_evidence.position_observations)::text || '|' ||
 (SELECT count(*) FROM broker_evidence.execution_observations)::text || '|' ||
 (SELECT count(*) FROM broker_evidence.trade_query_runs)::text;")" ||
  fail RESTORED_DB_QUERY_FAILED
[[ "$restore_state" == 'tradeops_evidence_recovery_check|17|5|0|0|0|0|0' ]] ||
  fail RESTORED_SCHEMA_INCORRECT

# Recheck source identity/emptiness to avoid asserting wrong state.
after="$(docker exec -u postgres "$source" psql -X -q -A -t -v ON_ERROR_STOP=1 -U postgres -d "$database" -c "
SELECT (SELECT count(*) FROM information_schema.tables WHERE table_schema='broker_evidence' AND table_type='BASE TABLE')::text || '|' ||
 (SELECT count(*) FROM broker_evidence.account_bindings)::text || '|' ||
 (SELECT count(*) FROM broker_evidence.position_snapshots)::text || '|' ||
 (SELECT count(*) FROM broker_evidence.position_observations)::text || '|' ||
 (SELECT count(*) FROM broker_evidence.execution_observations)::text || '|' ||
 (SELECT count(*) FROM broker_evidence.trade_query_runs)::text;")" || fail SOURCE_RECHECK_FAILED
[[ "$after" == '5|0|0|0|0|0' ]] || fail SOURCE_CHANGED_DURING_BACKUP

( cd -- "$backup_dir" && sha256sum -- "$(basename "$backup_file")" > "$(basename "$backup_file").sha256" ) ||
  fail SHA256_GENERATION_FAILED
chmod 600 -- "$backup_file.sha256"
( cd -- "$backup_dir" && sha256sum --check --status -- "$(basename "$backup_file").sha256" ) ||
  fail SHA256_RECHECK_FAILED
[[ "$(docker inspect -f '{{ index .Config.Labels "com.tradeops.schema_backup_nonce" }}' "$temp")" == "$nonce" ]] ||
  fail RESTORE_CONTAINER_OWNERSHIP_MISMATCH
docker rm -f "$temp" >/dev/null 2>&1 || fail RESTORE_CONTAINER_CLEANUP_FAILED
created=0
finished=1
printf 'TRADEOPS_PG17_SCHEMA_BACKUP_RESTORE_PASS source=tradeops_evidence major=17 schema_tables=5 owner_tables=5 independent_restore=PASS backup_checksum=PASS broker_rows=0 source_writes=0 deribit_calls=0\n'
printf 'TRADEOPS_PG17_SCHEMA_BACKUP_FILE=%s\n' "$backup_file"
