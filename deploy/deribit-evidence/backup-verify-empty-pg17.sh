#!/usr/bin/env bash
# TO-V2-29: owner-run backup and independent restore of the EMPTY persistent PG17.
# No schema creation, persistent database writes or Deribit calls are permitted.
set -Eeuo pipefail
umask 077

fail() {
    printf 'TRADEOPS_PG17_BACKUP_RESTORE_FAIL code=%s\n' "$1" >&2
    exit 1
}

[[ $# -eq 0 ]] || fail UNEXPECTED_ARGUMENTS

source_container='tradeops-deribit-evidence-pg17'
source_volume='tradeops-deribit-evidence-pg17-data'
source_db='tradeops_evidence'
restore_db='tradeops_evidence_restore_check'
backup_dir="$HOME/.local/share/tradeops/deribit-evidence-pg17/backups"

command -v docker >/dev/null 2>&1 || fail DOCKER_NOT_FOUND
docker info >/dev/null 2>&1 || fail DOCKER_UNAVAILABLE

# Read-only identity and volume checks. Never inspect shared-container ENV.
[[ "$(docker inspect -f '{{.State.Health.Status}}' "$source_container" 2>/dev/null || true)" == healthy ]] ||
    fail SOURCE_NOT_HEALTHY
[[ "$(docker inspect -f '{{ index .Config.Labels "com.tradeops.component" }}' "$source_container")" == 'deribit-readonly-evidence' ]] ||
    fail SOURCE_LABEL_MISMATCH
[[ "$(docker inspect -f '{{range .Mounts}}{{if eq .Destination "/var/lib/postgresql/data"}}{{.Name}}{{end}}{{end}}' "$source_container")" == "$source_volume" ]] ||
    fail SOURCE_VOLUME_MISMATCH

source_identity="$(docker exec -u postgres "$source_container" psql -X -q -A -t -v ON_ERROR_STOP=1 -U postgres -d "$source_db" -c "SELECT current_database() || '|' || (current_setting('server_version_num')::int / 10000)::text || '|' || (SELECT count(*)::text FROM pg_namespace WHERE nspname='broker_evidence')")" ||
    fail SOURCE_IDENTITY_QUERY_FAILED
[[ "$source_identity" == 'tradeops_evidence|17|0' ]] ||
    fail SOURCE_NOT_EMPTY_OR_UNEXPECTED_SCHEMA

# This workstream is for a PRE-MIGRATION backup only; do not use it after
# creating broker_evidence or for any future real Deribit position/trade data.
backup_parent="${backup_dir%/backups}"
[[ -d "$backup_parent" && ! -L "$backup_parent" ]] || fail BACKUP_PARENT_UNSAFE
[[ "$(stat -c '%u' "$backup_parent")" == "$(id -u)" ]] ||
    fail BACKUP_PARENT_OWNER
[[ ! -L "$backup_dir" ]] || fail BACKUP_DIR_SYMLINK
mkdir -p -- "$backup_dir"
[[ -d "$backup_dir" && ! -L "$backup_dir" ]] || fail BACKUP_DIR_UNSAFE
[[ "$(stat -c '%u' "$backup_dir")" == "$(id -u)" ]] || fail BACKUP_DIR_OWNER
chmod 700 -- "$backup_dir"

backup_file="$(mktemp "$backup_dir/pre-migration-$(date -u +%Y%m%dT%H%M%SZ)-XXXXXX.dump")" ||
    fail BACKUP_FILE_CREATION_FAILED
restore_container=''
verified=0

cleanup() {
    if [[ -n "$restore_container" ]]; then
        # Never remove a container without matching the per-run nonce label.
        label="$(docker inspect -f '{{ index .Config.Labels "com.tradeops.restore_nonce" }}' "$restore_container" 2>/dev/null || true)"
        if [[ "$label" == "$suffix" ]]; then
            docker rm -f "$restore_container" >/dev/null 2>&1 || true
        fi
    fi
    if [[ "$verified" != 1 ]]; then
        rm -f -- "$backup_file" "$backup_file.sha256"
    fi
}
trap cleanup EXIT

# pg_dump uses a consistent read snapshot of the live database; no writes.
# Docker exec has no -t: the custom binary archive must not be corrupted.
docker exec -u postgres "$source_container" pg_dump \
    --format=custom --no-owner --no-acl -U postgres -d "$source_db" \
    > "$backup_file" || fail PG_DUMP_FAILED
[[ -s "$backup_file" ]] || fail BACKUP_EMPTY
chmod 600 -- "$backup_file"

# Verify the archive can be listed by the same major PostgreSQL tooling.
docker exec -i "$source_container" pg_restore --list < "$backup_file" > /dev/null ||
    fail ARCHIVE_LIST_FAILED

# The restore target runs with NO NETWORK, NO published port, no access to
# source volumes, and disposable tmpfs PGDATA. Trust auth is restricted to
# this fully network-disabled isolated verification container only.
suffix="$(od -An -N5 -tx1 /dev/urandom | tr -d ' \n')"
[[ "$suffix" =~ ^[0-9a-f]{10}$ ]] || fail RESTORE_NAME_RANDOM_FAILED
restore_container="tradeops-empty-restore-check-$suffix"
if docker container inspect "$restore_container" >/dev/null 2>&1; then
    fail RESTORE_CONTAINER_ALREADY_EXISTS
fi

docker run --detach --rm \
    --name "$restore_container" \
    --network none \
    --tmpfs /var/lib/postgresql/data:rw,size=512m \
    --env POSTGRES_HOST_AUTH_METHOD=trust \
    --env POSTGRES_DB="$restore_db" \
    --label com.tradeops.component=one-shot-empty-db-restore-check \
    --label "com.tradeops.restore_nonce=$suffix" \
    postgres:17-bookworm > /dev/null ||
    fail RESTORE_CONTAINER_START_FAILED

ready=0
for _ in $(seq 1 45); do
    if docker exec -u postgres "$restore_container" pg_isready \
        -q -U postgres -d "$restore_db" >/dev/null 2>&1; then
        ready=1
        break
    fi
    sleep 2
done
[[ "$ready" == 1 ]] || fail RESTORE_CONTAINER_START_TIMEOUT

docker exec -i -u postgres "$restore_container" pg_restore \
    --exit-on-error --no-owner --no-acl -U postgres -d "$restore_db" \
    < "$backup_file" || fail INDEPENDENT_RESTORE_FAILED

restore_identity="$(docker exec -u postgres "$restore_container" psql \
    -X -q -A -t -v ON_ERROR_STOP=1 -U postgres -d "$restore_db" \
    -c "SELECT current_database() || '|' || (current_setting('server_version_num')::int / 10000)::text || '|' || (SELECT count(*)::text FROM pg_namespace WHERE nspname='broker_evidence')")" ||
    fail RESTORE_IDENTITY_QUERY_FAILED
[[ "$restore_identity" == 'tradeops_evidence_restore_check|17|0' ]] ||
    fail RESTORED_CONTENT_UNEXPECTED

# Retain one verified backup OUTSIDE the Docker data volume.
( cd -- "$backup_dir" && sha256sum -- "$(basename "$backup_file")" > "$(basename "$backup_file").sha256" ) ||
    fail BACKUP_SHA256_FAILED
chmod 600 -- "$backup_file.sha256"

# The temporary container must be gone before we claim PASS.
[[ "$(docker inspect -f '{{ index .Config.Labels "com.tradeops.restore_nonce" }}' "$restore_container")" == "$suffix" ]] ||
    fail RESTORE_CONTAINER_OWNERSHIP_MISMATCH
docker rm -f "$restore_container" >/dev/null 2>&1 ||
    fail RESTORE_CONTAINER_CLEANUP_FAILED
restore_container=''
verified=1
printf 'TRADEOPS_PG17_BACKUP_RESTORE_PASS source=tradeops_evidence major=17 schema_present=0 isolated_restore=PASS backup_saved=yes backup_sha256=yes persistent_writes=0 broker_calls=0\n'
printf 'TRADEOPS_PG17_BACKUP_FILE=%s\n' "$backup_file"
