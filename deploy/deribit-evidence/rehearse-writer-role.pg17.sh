#!/usr/bin/env bash
# TO-V2-33: validate role grants with real SQL only in disposable PG17.
# No permanent role provisioning and no broker/API/Worker access.
set -Eeuo pipefail
umask 077
fail() { printf 'TRADEOPS_WRITER_ROLE_REHEARSAL_FAIL code=%s\n' "$1" >&2; exit 1; }
[[ $# -eq 3 ]] || fail PINNED_INPUTS_REQUIRED
for input in "$@"; do
  [[ -f "$input" && ! -L "$input" ]] || fail UNSAFE_INPUT
done
schema_sql="$1"
writer_sql="$2"
fixture_sql="$3"
source='tradeops-deribit-evidence-pg17'
volume='tradeops-deribit-evidence-pg17-data'
backup_dir="$HOME/.local/share/tradeops/deribit-evidence-pg17/backups"
command -v docker >/dev/null 2>&1 || fail DOCKER_MISSING
docker info >/dev/null 2>&1 || fail DOCKER_ENGINE_UNAVAILABLE

[[ "$(docker inspect -f '{{.State.Health.Status}}' "$source" 2>/dev/null || true)" == healthy ]] ||
  fail SOURCE_UNHEALTHY
[[ "$(docker inspect -f '{{ index .Config.Labels "com.tradeops.project" }}' "$source")" == TradeOps-v2 ]] ||
  fail SOURCE_PROJECT_MISMATCH
[[ "$(docker inspect -f '{{ index .Config.Labels "com.tradeops.component" }}' "$source")" == deribit-readonly-evidence ]] ||
  fail SOURCE_COMPONENT_MISMATCH
[[ "$(docker inspect -f '{{range .Mounts}}{{if eq .Destination "/var/lib/postgresql/data"}}{{.Name}}{{end}}{{end}}' "$source")" == "$volume" ]] ||
  fail SOURCE_VOLUME_MISMATCH

# The source is only queried, never modified.
source_state="$(docker exec -u postgres "$source" psql -X -q -A -t \
  -v ON_ERROR_STOP=1 -U postgres -d tradeops_evidence -c "
SELECT current_database() || '|' ||
 (current_setting('server_version_num')::integer / 10000)::text || '|' ||
 (SELECT count(*) FROM information_schema.tables
  WHERE table_schema='broker_evidence' AND table_type='BASE TABLE')::text || '|' ||
 (SELECT count(*) FROM pg_class c
  JOIN pg_namespace n ON n.oid=c.relnamespace
  JOIN pg_roles r ON r.oid=c.relowner
  WHERE n.nspname='broker_evidence' AND c.relkind='r'
    AND r.rolname='tradeops_evidence_owner')::text || '|' ||
 (SELECT count(*) FROM pg_roles WHERE rolname='tradeops_evidence_writer')::text || '|' ||
 (SELECT count(*) FROM broker_evidence.account_bindings)::text || '|' ||
 (SELECT count(*) FROM broker_evidence.position_snapshots)::text || '|' ||
 (SELECT count(*) FROM broker_evidence.position_observations)::text || '|' ||
 (SELECT count(*) FROM broker_evidence.execution_observations)::text || '|' ||
 (SELECT count(*) FROM broker_evidence.trade_query_runs)::text;
")" || fail SOURCE_CHECK_FAILED
[[ "$source_state" == 'tradeops_evidence|17|5|5|0|0|0|0|0|0' ]] ||
  fail SOURCE_ALREADY_HAS_WRITER_OR_DATA

[[ -d "$backup_dir" && ! -L "$backup_dir" ]] || fail POST_SCHEMA_BACKUP_MISSING
[[ "$(stat -c %u "$backup_dir")" == "$(id -u)" ]] ||
  fail BACKUP_DIR_OWNERSHIP_MISMATCH
verified=0
while IFS= read -r -d '' check; do
  [[ -f "$check" && ! -L "$check" ]] || fail BACKUP_CHECKSUM_UNSAFE
  (cd -- "$backup_dir" && sha256sum --check --status -- "$(basename "$check")") ||
    fail POST_SCHEMA_BACKUP_CHECKSUM_MISMATCH
  verified=$((verified+1))
done < <(find "$backup_dir" -maxdepth 1 -type f \
  -name 'post-schema-*.dump.sha256' -print0)
(( verified > 0 )) || fail POST_SCHEMA_BACKUP_NOT_VERIFIED

nonce="$(od -An -N6 -tx1 /dev/urandom | tr -d ' \n')"
[[ "$nonce" =~ ^[a-f0-9]{12}$ ]] || fail NONCE_FAILED
temporary="tradeops-writer-role-lab-$nonce"
docker inspect "$temporary" >/dev/null 2>&1 && fail TEMP_ALREADY_EXISTS
created=0
cleanup() {
  if [[ "$created" == 1 ]]; then
    label="$(docker inspect -f '{{ index .Config.Labels "com.tradeops.writer_lab_nonce" }}' "$temporary" 2>/dev/null || true)"
    [[ "$label" == "$nonce" ]] && docker rm -f "$temporary" >/dev/null 2>&1 || true
  fi
}
trap cleanup EXIT

docker run --detach --rm --name "$temporary" \
  --network none \
  --tmpfs /var/lib/postgresql/data:rw,size=512m \
  --env POSTGRES_HOST_AUTH_METHOD=trust \
  --env POSTGRES_DB=tradeops_evidence \
  --label "com.tradeops.writer_lab_nonce=$nonce" \
  postgres:17-bookworm >/dev/null || fail LAB_START_FAILED
created=1
ready=0
for _ in $(seq 1 45); do
  if docker exec -u postgres "$temporary" pg_isready -q \
    -U postgres -d tradeops_evidence >/dev/null 2>&1; then
    ready=1; break
  fi
  sleep 2
done
[[ "$ready" == 1 ]] || fail LAB_NOT_READY

# Recreate the exact schema proposal, solely inside disposable database.
docker exec -i -u postgres "$temporary" psql -X -q -v ON_ERROR_STOP=1 \
  -U postgres -d tradeops_evidence < "$schema_sql" >/dev/null ||
  fail LAB_SCHEMA_SETUP_FAILED

# Recreate the actual persistent NOLOGIN schema owner in the isolated lab.
docker exec -i -u postgres "$temporary" psql -X -q -v ON_ERROR_STOP=1 \
  -U postgres -d tradeops_evidence >/dev/null <<'OWNER'
BEGIN;
CREATE ROLE tradeops_evidence_owner NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION;
ALTER SCHEMA broker_evidence OWNER TO tradeops_evidence_owner;
ALTER TABLE broker_evidence.account_bindings OWNER TO tradeops_evidence_owner;
ALTER TABLE broker_evidence.position_snapshots OWNER TO tradeops_evidence_owner;
ALTER TABLE broker_evidence.position_observations OWNER TO tradeops_evidence_owner;
ALTER TABLE broker_evidence.execution_observations OWNER TO tradeops_evidence_owner;
ALTER TABLE broker_evidence.trade_query_runs OWNER TO tradeops_evidence_owner;
COMMIT;
OWNER

# Apply unmodified proposed GRANT DDL to ephemeral server only.
docker exec -i -u postgres "$temporary" psql -X -q -v ON_ERROR_STOP=1 \
  -U postgres -d tradeops_evidence < "$writer_sql" >/dev/null ||
  fail WRITER_ROLE_GRANTS_FAILED

# Actual PostgreSQL role-based permission allow/deny tests with synthetic rows,
# all rolled back. Role is NOLOGIN and cannot authenticate externally.
docker exec -i -u postgres "$temporary" psql -X -q -v ON_ERROR_STOP=1 \
  -U postgres -d tradeops_evidence < "$fixture_sql" >/dev/null ||
  fail WRITER_ROLE_PERMISSIONS_FAILED

role_state="$(docker exec -u postgres "$temporary" psql -X -q -A -t \
  -v ON_ERROR_STOP=1 -U postgres -d tradeops_evidence -c "
SELECT (SELECT count(*) FROM pg_roles
        WHERE rolname='tradeops_evidence_writer' AND NOT rolcanlogin)::text || '|' ||
       (SELECT count(*) FROM broker_evidence.account_bindings)::text || '|' ||
       (SELECT count(*) FROM broker_evidence.position_snapshots)::text || '|' ||
       (SELECT count(*) FROM broker_evidence.position_observations)::text;
")" || fail WRITER_LAB_FINAL_QUERY_FAILED
[[ "$role_state" == '1|0|0|0' ]] || fail WRITER_LAB_ROLLBACK_FAILED

after="$(docker exec -u postgres "$source" psql -X -q -A -t -v ON_ERROR_STOP=1 \
  -U postgres -d tradeops_evidence -c "
SELECT (SELECT count(*) FROM pg_roles WHERE rolname='tradeops_evidence_writer')::text || '|' ||
       (SELECT count(*) FROM broker_evidence.account_bindings)::text || '|' ||
       (SELECT count(*) FROM broker_evidence.position_snapshots)::text;
")" || fail SOURCE_FINAL_QUERY_FAILED
[[ "$after" == '0|0|0' ]] || fail SOURCE_CHANGED

[[ "$(docker inspect -f '{{ index .Config.Labels "com.tradeops.writer_lab_nonce" }}' "$temporary")" == "$nonce" ]] ||
  fail LAB_OWNERSHIP_MISMATCH
docker rm -f "$temporary" >/dev/null 2>&1 || fail LAB_CLEANUP_FAILED
created=0
printf 'TRADEOPS_PG17_WRITER_ROLE_REHEARSAL_PASS source=tradeops_evidence major=17 writer_login=DISABLED account_binding_select=PASS positions_insert=PASS update_delete_ddl=DENIED executions_insert=DENIED lab_rollback=PASS persistent_roles_created=0 persistent_writes=0 deribit_calls=0\n'
