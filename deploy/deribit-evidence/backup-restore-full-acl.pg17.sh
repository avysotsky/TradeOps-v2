#!/usr/bin/env bash
# TO-V2-35: OWNER-LOCAL, READ-ONLY evidence database backup with ownership
# and grants intact, verified in a new unnetworked PostgreSQL 17 container.
# This PRE-DATA gate deliberately refuses any broker evidence rows.
set -Eeuo pipefail
umask 077
fail() { printf 'TRADEOPS_PG17_ACL_RECOVERY_FAIL code=%s\n' "$1" >&2; exit 1; }
[[ $# -eq 1 && -f "$1" && ! -L "$1" ]] || fail PINNED_ROLES_SQL_REQUIRED
role_sql="$1"
source='tradeops-deribit-evidence-pg17'
volume='tradeops-deribit-evidence-pg17-data'
backup_dir="$HOME/.local/share/tradeops/deribit-evidence-pg17/backups"
command -v docker >/dev/null 2>&1 || fail DOCKER_MISSING
docker info >/dev/null 2>&1 || fail DOCKER_UNAVAILABLE
[[ "$(docker inspect -f '{{.State.Health.Status}}' "$source" 2>/dev/null || true)" == healthy ]] || fail SOURCE_UNHEALTHY
[[ "$(docker inspect -f '{{ index .Config.Labels "com.tradeops.project" }}' "$source")" == TradeOps-v2 ]] || fail SOURCE_PROJECT_MISMATCH
[[ "$(docker inspect -f '{{ index .Config.Labels "com.tradeops.component" }}' "$source")" == deribit-readonly-evidence ]] || fail SOURCE_COMPONENT_MISMATCH
[[ "$(docker inspect -f '{{range .Mounts}}{{if eq .Destination "/var/lib/postgresql/data"}}{{.Name}}{{end}}{{end}}' "$source")" == "$volume" ]] || fail SOURCE_VOLUME_MISMATCH

# The same audit is performed independently on the permanent source and
# the temporary restored copy, including real grants and object ownership.
audit() {
    docker exec -u postgres "$1" psql -X -q -A -t -v ON_ERROR_STOP=1 \
        -U postgres -d tradeops_evidence -c "
SELECT current_database() || '|' ||
  (current_setting('server_version_num')::integer / 10000)::text || '|' ||
  (SELECT count(*) FROM information_schema.tables
    WHERE table_schema='broker_evidence' AND table_type='BASE TABLE')::text || '|' ||
  (SELECT count(*) FROM pg_namespace n JOIN pg_roles r ON n.nspowner=r.oid
    WHERE n.nspname='broker_evidence'
      AND r.rolname='tradeops_evidence_owner')::text || '|' ||
  (SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
    JOIN pg_roles r ON r.oid=c.relowner
    WHERE n.nspname='broker_evidence' AND c.relkind='r'
      AND r.rolname='tradeops_evidence_owner')::text || '|' ||
  (SELECT count(*) FROM pg_roles WHERE rolname='tradeops_evidence_owner'
    AND NOT rolcanlogin AND NOT rolsuper AND NOT rolcreatedb
    AND NOT rolcreaterole)::text || '|' ||
  (SELECT count(*) FROM pg_roles WHERE rolname='tradeops_evidence_writer'
    AND NOT rolcanlogin AND NOT rolsuper AND NOT rolcreatedb
    AND NOT rolcreaterole)::text || '|' ||
  (SELECT count(*) FROM broker_evidence.account_bindings
   )::text || '|' ||
  (SELECT count(*) FROM broker_evidence.position_snapshots
   )::text || '|' ||
  (SELECT count(*) FROM broker_evidence.position_observations
   )::text || '|' ||
  (SELECT count(*) FROM broker_evidence.execution_observations
   )::text || '|' ||
  (SELECT count(*) FROM broker_evidence.trade_query_runs
   )::text || '|' ||
  has_database_privilege('tradeops_evidence_writer','tradeops_evidence','CONNECT')::int::text || '|' ||
  has_database_privilege('tradeops_evidence_writer','tradeops_evidence','CREATE')::int::text || '|' ||
  has_schema_privilege('tradeops_evidence_writer','broker_evidence','USAGE')::int::text || '|' ||
  has_schema_privilege('tradeops_evidence_writer','broker_evidence','CREATE')::int::text || '|' ||
  has_table_privilege('tradeops_evidence_writer','broker_evidence.account_bindings','SELECT')::int::text || '|' ||
  has_table_privilege('tradeops_evidence_writer','broker_evidence.account_bindings','INSERT')::int::text || '|' ||
  has_table_privilege('tradeops_evidence_writer','broker_evidence.position_snapshots','INSERT')::int::text || '|' ||
  has_table_privilege('tradeops_evidence_writer','broker_evidence.position_observations','INSERT')::int::text || '|' ||
  has_table_privilege('tradeops_evidence_writer','broker_evidence.position_snapshots','SELECT')::int::text || '|' ||
  has_table_privilege('tradeops_evidence_writer','broker_evidence.position_snapshots','UPDATE')::int::text || '|' ||
  has_table_privilege('tradeops_evidence_writer','broker_evidence.position_snapshots','DELETE')::int::text || '|' ||
  has_table_privilege('tradeops_evidence_writer','broker_evidence.execution_observations','INSERT')::int::text || '|' ||
  has_table_privilege('tradeops_evidence_writer','broker_evidence.trade_query_runs','INSERT')::int::text;
"
}
expected='tradeops_evidence|17|5|1|5|1|1|0|0|0|0|0|1|0|1|0|1|0|1|1|0|0|0|0|0'
source_state="$(audit "$source")" || fail SOURCE_AUDIT_FAILED
[[ "$source_state" == "$expected" ]] || fail SOURCE_ROLE_ACL_OR_DATA_MISMATCH

backup_parent="$HOME/.local/share/tradeops/deribit-evidence-pg17"
[[ -d "$backup_parent" && ! -L "$backup_parent" ]] || fail BACKUP_PARENT_UNSAFE
[[ "$(stat -c %u "$backup_parent")" == "$(id -u)" ]] || fail BACKUP_PARENT_OWNER
[[ ! -L "$backup_dir" ]] || fail BACKUP_DIR_SYMLINK
mkdir -p -- "$backup_dir"
[[ "$(stat -c %u "$backup_dir")" == "$(id -u)" ]] || fail BACKUP_DIR_OWNER
chmod 700 -- "$backup_dir"
bundle="$(mktemp -d "$backup_dir/role-acl-recovery-$(date -u +%Y%m%dT%H%M%SZ)-XXXXXX")" || fail BUNDLE_CREATE_FAILED
chmod 700 -- "$bundle"
dump="$bundle/tradeops_evidence.dump"
roles="$bundle/restore-known-evidence-roles.pg17.sql"
hashes="$bundle/SHA256SUMS"
temp=''
nonce=''
created=0
verified=0
cleanup() {
  if [[ "$created" == 1 ]]; then
    actual="$(docker inspect -f '{{ index .Config.Labels "com.tradeops.acl_recovery_nonce" }}' "$temp" 2>/dev/null || true)"
    if [[ "$actual" == "$nonce" ]]; then
      docker rm -f "$temp" >/dev/null 2>&1 || true
    fi
  fi
  if [[ "$verified" != 1 ]]; then
    rm -f -- "$dump" "$roles" "$hashes"
    rmdir -- "$bundle" 2>/dev/null || true
  fi
}
trap cleanup EXIT

# Critically NO --no-owner and NO --no-acl. These flags in older
# empty-schema backup made an insufficient disaster-recovery artifact.
cp -- "$role_sql" "$roles" || fail ROLES_BOOTSTRAP_COPY_FAILED
chmod 600 -- "$roles"
docker exec -u postgres "$source" pg_dump --format=custom \
  --username=postgres --dbname=tradeops_evidence > "$dump" ||
  fail PG_DUMP_FAILED
[[ -s "$dump" ]] || fail PG_DUMP_EMPTY
chmod 600 -- "$dump"
docker exec -i "$source" pg_restore --list < "$dump" >/dev/null ||
  fail PG_RESTORE_LIST_FAILED

nonce="$(od -An -N6 -tx1 /dev/urandom | tr -d ' \n')"
[[ "$nonce" =~ ^[a-f0-9]{12}$ ]] || fail NONCE_FAILED
temp="tradeops-role-acl-recovery-$nonce"
docker inspect "$temp" >/dev/null 2>&1 && fail TEMP_CONTAINER_COLLISION
docker run --detach --rm --name "$temp" \
  --network none --tmpfs /var/lib/postgresql/data:rw,size=512m \
  --env POSTGRES_HOST_AUTH_METHOD=trust \
  --env POSTGRES_DB=postgres \
  --label "com.tradeops.acl_recovery_nonce=$nonce" \
  postgres:17-bookworm >/dev/null || fail TEMP_START_FAILED
created=1
ready=0
for _ in $(seq 1 45); do
  if docker exec -u postgres "$temp" pg_isready -q -U postgres -d postgres >/dev/null 2>&1; then
    ready=1; break
  fi
  sleep 2
done
[[ "$ready" == 1 ]] || fail TEMP_READY_TIMEOUT

# The pinned roles script creates ONLY our two NOLOGIN roles. pg_restore -C
# then creates the original database and restores its full owner and ACLs.
docker exec -i -u postgres "$temp" psql -X -q -v ON_ERROR_STOP=1 \
  -U postgres -d postgres < "$roles" >/dev/null ||
  fail RESTORE_ROLES_FAILED
docker exec -i -u postgres "$temp" pg_restore --exit-on-error \
  --create --username=postgres --dbname=postgres < "$dump" ||
  fail RESTORE_DATABASE_AND_PRIVILEGES_FAILED

restored_state="$(audit "$temp")" || fail RESTORE_AUDIT_FAILED
[[ "$restored_state" == "$expected" ]] || fail OWNER_OR_ACL_RECOVERY_FAILED
after_state="$(audit "$source")" || fail SOURCE_POST_AUDIT_FAILED
[[ "$after_state" == "$expected" ]] || fail SOURCE_CHANGED_DURING_RECOVERY

(
 cd -- "$bundle"
 sha256sum -- tradeops_evidence.dump restore-known-evidence-roles.pg17.sql > SHA256SUMS
 sha256sum --check --status -- SHA256SUMS
) || fail BACKUP_CHECKSUM_FAILED
chmod 600 -- "$hashes"
[[ "$(docker inspect -f '{{ index .Config.Labels "com.tradeops.acl_recovery_nonce" }}' "$temp")" == "$nonce" ]] ||
  fail TEMP_OWNERSHIP_MISMATCH
docker rm -f "$temp" >/dev/null 2>&1 || fail TEMP_CLEANUP_FAILED
created=0
verified=1
printf 'TRADEOPS_PG17_ROLE_ACL_RECOVERY_PASS source=tradeops_evidence major=17 tables=5 owner_role=NOLOGIN writer_role=NOLOGIN grants=RESTORED denies=ENFORCED evidence_rows=0 independent_restore=PASS source_writes=0 deribit_calls=0\n'
printf 'TRADEOPS_PG17_ROLE_ACL_BUNDLE=%s\n' "$bundle"
