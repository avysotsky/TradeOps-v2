#!/usr/bin/env bash
# TO-V2-34: ONE-TIME OWNER-APPROVED ROLE-ONLY DDL ON DEDICATED PG17.
# Does not activate login, create account bindings, import Deribit, or trade.
set -Eeuo pipefail
umask 077
fail() { printf 'TRADEOPS_PG17_WRITER_INSTALL_FAIL code=%s\n' "$1" >&2; exit 1; }
[[ $# -eq 2 && -f "$1" && ! -L "$1" ]] || fail PINNED_SQL_REQUIRED
[[ "$2" == 'I_APPROVE_INSTALL_NOLOGIN_WRITER_TRADEOPS_EVIDENCE' ]] || fail OWNER_APPROVAL_MISSING
sql_file="$1"
source='tradeops-deribit-evidence-pg17'
volume='tradeops-deribit-evidence-pg17-data'
database='tradeops_evidence'
backup_dir="$HOME/.local/share/tradeops/deribit-evidence-pg17/backups"

command -v docker >/dev/null 2>&1 || fail DOCKER_NOT_FOUND
docker info >/dev/null 2>&1 || fail DOCKER_UNAVAILABLE
[[ "$(docker inspect -f '{{.State.Health.Status}}' "$source" 2>/dev/null || true)" == healthy ]] || fail SOURCE_NOT_HEALTHY
[[ "$(docker inspect -f '{{ index .Config.Labels "com.tradeops.project" }}' "$source")" == 'TradeOps-v2' ]] || fail PROJECT_LABEL_MISMATCH
[[ "$(docker inspect -f '{{ index .Config.Labels "com.tradeops.component" }}' "$source")" == 'deribit-readonly-evidence' ]] || fail COMPONENT_LABEL_MISMATCH
[[ "$(docker inspect -f '{{range .Mounts}}{{if eq .Destination "/var/lib/postgresql/data"}}{{.Name}}{{end}}{{end}}' "$source")" == "$volume" ]] || fail SOURCE_VOLUME_MISMATCH

# This preflight is read-only; the SQL proposal itself repeats all identity,
# existing-role, ownership and zero-row checks INSIDE its DDL transaction.
state="$(docker exec -u postgres "$source" psql -X -q -A -t \
  -v ON_ERROR_STOP=1 -U postgres -d "$database" -c "
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
")" || fail PRECHECK_QUERY_FAILED
[[ "$state" == 'tradeops_evidence|17|5|5|0|0|0|0|0|0' ]] || fail UNEXPECTED_SOURCE_OR_EXISTING_ROLE

# Mandatory post-schema backup integrity. The backup must have previously
# passed a full isolated restore; checksum verification alone is insufficient.
[[ -d "$backup_dir" && ! -L "$backup_dir" ]] || fail SCHEMA_BACKUP_DIR_MISSING
[[ "$(stat -c %u "$backup_dir")" == "$(id -u)" ]] || fail SCHEMA_BACKUP_DIR_OWNER
count=0
while IFS= read -r -d '' checksum; do
  [[ -f "$checksum" && ! -L "$checksum" ]] || fail SCHEMA_BACKUP_CHECKSUM_UNSAFE
  (cd -- "$backup_dir" && sha256sum --check --status -- "$(basename "$checksum")") ||
     fail SCHEMA_BACKUP_CHECKSUM_INVALID
  count=$((count + 1))
done < <(find "$backup_dir" -maxdepth 1 -type f -name 'post-schema-*.dump.sha256' -print0)
(( count > 0 )) || fail SCHEMA_BACKUP_NOT_FOUND

printf 'TRADEOPS_PG17_WRITER_INSTALL_BEGIN database=tradeops_evidence role=tradeops_evidence_writer login=DISABLED\n'

# This is the ONLY persistent write: one explicitly approved transaction
# creating a NOLOGIN least-privilege role. No shell SQL interpolation.
docker exec -i -u postgres "$source" psql -X -q -v ON_ERROR_STOP=1 \
  -U postgres -d "$database" < "$sql_file" >/dev/null ||
  fail WRITER_ROLE_TRANSACTION_FAILED

# Read-only post-commit audit; do not log any account/trade contents.
post="$(docker exec -u postgres "$source" psql -X -q -A -t \
  -v ON_ERROR_STOP=1 -U postgres -d "$database" -c "
SELECT
 (SELECT count(*) FROM pg_roles
  WHERE rolname='tradeops_evidence_writer' AND NOT rolcanlogin
    AND NOT rolsuper AND NOT rolcreatedb AND NOT rolcreaterole
    AND NOT rolreplication)::text || '|' ||
 (SELECT count(*) FROM information_schema.tables
  WHERE table_schema='broker_evidence' AND table_type='BASE TABLE')::text || '|' ||
 (SELECT count(*) FROM broker_evidence.account_bindings)::text || '|' ||
 (SELECT count(*) FROM broker_evidence.position_snapshots)::text || '|' ||
 (SELECT count(*) FROM broker_evidence.position_observations)::text || '|' ||
 (SELECT count(*) FROM broker_evidence.execution_observations)::text || '|' ||
 (SELECT count(*) FROM broker_evidence.trade_query_runs)::text || '|' ||
 has_table_privilege('tradeops_evidence_writer','broker_evidence.account_bindings','SELECT')::int::text || '|' ||
 has_table_privilege('tradeops_evidence_writer','broker_evidence.position_snapshots','INSERT')::int::text || '|' ||
 has_table_privilege('tradeops_evidence_writer','broker_evidence.position_observations','INSERT')::int::text || '|' ||
 has_table_privilege('tradeops_evidence_writer','broker_evidence.position_snapshots','UPDATE')::int::text || '|' ||
 has_table_privilege('tradeops_evidence_writer','broker_evidence.position_snapshots','DELETE')::int::text || '|' ||
 has_table_privilege('tradeops_evidence_writer','broker_evidence.execution_observations','INSERT')::int::text || '|' ||
 has_table_privilege('tradeops_evidence_writer','broker_evidence.account_bindings','INSERT')::int::text;
")" || fail WRITER_POSTCHECK_QUERY_FAILED
[[ "$post" == '1|5|0|0|0|0|0|1|1|1|0|0|0|0' ]] ||
  fail WRITER_POSTCHECK_UNEXPECTED

printf 'TRADEOPS_PG17_WRITER_INSTALL_PASS database=tradeops_evidence major=17 role=tradeops_evidence_writer login=DISABLED binding_select=YES position_insert=YES update_delete=DENIED execution_insert=DENIED role_count=1 broker_rows=0 deribit_calls=0\n'
