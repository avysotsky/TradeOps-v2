#!/usr/bin/env bash
# TO-V2-31: PERMANENT schema migration, deliberately NOT an automatic command.
# Must be invoked by owner only after a separate explicit approval of this
# precise target + reviewed pinned SHA. NEVER calls Deribit or changes Orders/Fills.
set -Eeuo pipefail
umask 077

fail() { printf 'TRADEOPS_PERSISTENT_SCHEMA_MIGRATION_FAIL code=%s\n' "$1" >&2; exit 1; }

[[ $# -eq 2 && -f "$1" && ! -L "$1" ]] || fail APPROVED_SQL_ARGUMENTS_REQUIRED
sql_file="$1"
[[ "$2" == 'I_APPROVE_SCHEMA_ONLY_TRADEOPS_EVIDENCE_PG17' ]] ||
  fail OWNER_APPROVAL_TOKEN_MISSING

source='tradeops-deribit-evidence-pg17'
volume='tradeops-deribit-evidence-pg17-data'
database='tradeops_evidence'
schema='broker_evidence'
schema_owner='tradeops_evidence_owner'
backup_dir="$HOME/.local/share/tradeops/deribit-evidence-pg17/backups"

command -v docker >/dev/null 2>&1 || fail DOCKER_MISSING
docker info >/dev/null 2>&1 || fail DOCKER_UNAVAILABLE
[[ "$(docker inspect -f '{{.State.Health.Status}}' "$source" 2>/dev/null || true)" == healthy ]] ||
  fail SOURCE_UNHEALTHY
[[ "$(docker inspect -f '{{ index .Config.Labels "com.tradeops.project" }}' "$source")" == 'TradeOps-v2' ]] ||
  fail PROJECT_LABEL_MISMATCH
[[ "$(docker inspect -f '{{ index .Config.Labels "com.tradeops.component" }}' "$source")" == deribit-readonly-evidence ]] ||
  fail COMPONENT_LABEL_MISMATCH
[[ "$(docker inspect -f '{{range .Mounts}}{{if eq .Destination "/var/lib/postgresql/data"}}{{.Name}}{{end}}{{end}}' "$source")" == "$volume" ]] ||
  fail SOURCE_VOLUME_MISMATCH

# Check all source identities BEFORE sending any persistent migration SQL.
identity="$(docker exec -u postgres "$source" psql -X -q -A -t -v ON_ERROR_STOP=1 \
  -U postgres -d "$database" -c \
  "SELECT current_database() || '|' || (current_setting('server_version_num')::int / 10000)::text || '|' || (SELECT count(*) FROM pg_namespace WHERE nspname='broker_evidence')::text || '|' || (SELECT count(*) FROM pg_roles WHERE rolname='tradeops_evidence_owner')::text")" ||
  fail SOURCE_IDENTITY_QUERY_FAILED
[[ "$identity" == 'tradeops_evidence|17|0|0' ]] ||
  fail SOURCE_ALREADY_MIGRATED_OR_ROLE_COLLISION

[[ -d "$backup_dir" && ! -L "$backup_dir" ]] || fail BACKUP_DIR_MISSING
[[ "$(stat -c %u "$backup_dir")" == "$(id -u)" ]] || fail BACKUP_DIR_OWNER
verified=0
while IFS= read -r -d '' check; do
  [[ -f "$check" && ! -L "$check" ]] || fail BACKUP_CHECKSUM_PATH_UNSAFE
  (cd -- "$backup_dir" && sha256sum --check --status -- "$(basename "$check")") ||
    fail BACKUP_CHECKSUM_MISMATCH
  verified=$((verified + 1))
done < <(find "$backup_dir" -maxdepth 1 -type f -name 'pre-migration-*.dump.sha256' -print0)
(( verified > 0 )) || fail VERIFIED_BACKUP_MISSING

# The migration proposal was previously rehearsed at its *same immutable SHA*.
# Strip exactly its single BEGIN and COMMIT so it can be included in ONE
# transaction with owner role creation and temporary CREATE privilege.
[[ "$(grep -xc 'BEGIN;' "$sql_file")" == 1 ]] || fail SQL_BEGIN_UNEXPECTED
[[ "$(grep -xc 'COMMIT;' "$sql_file")" == 1 ]] || fail SQL_COMMIT_UNEXPECTED
[[ "$(tail -n 1 "$sql_file")" == 'COMMIT;' ]] || fail SQL_LAST_LINE_UNEXPECTED
[[ "$(grep -c '^CREATE TABLE broker_evidence\.' "$sql_file")" == 5 ]] ||
  fail SQL_TABLE_COUNT_UNEXPECTED

printf 'TRADEOPS_PERSISTENT_SCHEMA_MIGRATION_BEGIN database=tradeops_evidence postgres_major=17 mode=schema_only\n'

# PostgreSQL transactional DDL; a failing psql exits with the transaction
# uncommitted, including CREATE ROLE. No data is deleted.
{
cat <<'PREFLIGHT_SQL'
BEGIN;
DO $guard$
BEGIN
  IF current_database() <> 'tradeops_evidence'
     OR current_setting('server_version_num')::integer < 170000
     OR current_setting('server_version_num')::integer >= 180000
     OR to_regnamespace('broker_evidence') IS NOT NULL
     OR EXISTS (SELECT 1 FROM pg_roles WHERE rolname='tradeops_evidence_owner')
  THEN
    RAISE EXCEPTION 'persistent PG17 identity or schema owner guard failed';
  END IF;
END $guard$;
CREATE ROLE tradeops_evidence_owner NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION;
GRANT CREATE ON DATABASE tradeops_evidence TO tradeops_evidence_owner;
SET ROLE tradeops_evidence_owner;
PREFLIGHT_SQL
awk '$0 != "BEGIN;" && $0 != "COMMIT;" { print }' "$sql_file"
cat <<'FINISH_SQL'
RESET ROLE;
REVOKE CREATE ON DATABASE tradeops_evidence FROM tradeops_evidence_owner;
COMMIT;
FINISH_SQL
} | docker exec -i -u postgres "$source" psql -X -q -v ON_ERROR_STOP=1 \
    -U postgres -d "$database" >/dev/null ||
    fail PERSISTENT_MIGRATION_TRANSACTION_FAILED

# Read-only independent post-commit assertions (no account binding inserted).
post="$(docker exec -u postgres "$source" psql -X -q -A -t -v ON_ERROR_STOP=1 \
  -U postgres -d "$database" -c \
  "SELECT (SELECT count(*) FROM information_schema.tables WHERE table_schema='broker_evidence' AND table_type='BASE TABLE')::text || '|' || (SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace JOIN pg_roles r ON r.oid=c.relowner WHERE n.nspname='broker_evidence' AND c.relkind='r' AND r.rolname='tradeops_evidence_owner')::text || '|' || (SELECT count(*) FROM broker_evidence.account_bindings)::text || '|' || (SELECT count(*) FROM broker_evidence.position_snapshots)::text || '|' || (SELECT count(*) FROM broker_evidence.execution_observations)::text || '|' || (SELECT count(*) FROM broker_evidence.trade_query_runs)::text")" ||
  fail POST_MIGRATION_READ_FAILED
[[ "$post" == '5|5|0|0|0|0' ]] || fail POST_MIGRATION_TABLE_OR_OWNER_MISMATCH

printf 'TRADEOPS_PERSISTENT_SCHEMA_MIGRATION_PASS database=tradeops_evidence major=17 schema=broker_evidence tables=5 owned_by_nologin_role=5 account_bindings=0 positions=0 executions=0 trade_query_runs=0 deribit_calls=0 legacy_tables_unchanged=true\n'
