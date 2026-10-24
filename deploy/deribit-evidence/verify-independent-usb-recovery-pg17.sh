#!/usr/bin/env bash
# TO-V2-37: independent encrypted USB copy decryption and PG17 restore.
# This script NEVER reads or writes the persistent PG17 or original WSL
# recovery bundle. It reads one encrypted artifact from the selected USB.
set -Eeuo pipefail
set +x
umask 077
fail() { printf 'TRADEOPS_USB_PG17_RECOVERY_FAIL code=%s\n' "$1" >&2; exit 1; }
[[ $# -eq 1 && -f "$1" && ! -L "$1" ]] || fail ENCRYPTED_USB_FILE_REQUIRED
cipher="$1"
sidecar="$cipher.sha256"
[[ -f "$sidecar" && ! -L "$sidecar" ]] || fail USB_SHA256_FILE_REQUIRED
case "$cipher" in /mnt/[a-z]/*) ;; *) fail NOT_A_WINDOWS_MOUNT ;; esac
[[ "$cipher" == *.tar.gpg ]] || fail NOT_A_GPG_RECOVERY_BUNDLE

for tool in sha256sum gpg tar docker od mktemp stat; do
  command -v "$tool" >/dev/null 2>&1 || fail REQUIRED_TOOL_MISSING
done
docker info >/dev/null 2>&1 || fail DOCKER_ENGINE_UNAVAILABLE
[[ -r /dev/tty && -w /dev/tty ]] || fail INTERACTIVE_TTY_REQUIRED
(
  cd -- "$(dirname -- "$cipher")"
  sha256sum --check --status -- "$(basename -- "$sidecar")"
) || fail USB_ENCRYPTED_SHA256_MISMATCH

root="$HOME/.local/share/tradeops/deribit-evidence-pg17"
[[ -d "$root" && ! -L "$root" && "$(stat -c '%u' "$root")" == "$(id -u)" ]] ||
  fail PRIVATE_WSL_TEST_ROOT_UNSAFE
staging="$(mktemp -d "$root/usb-recovery-test-XXXXXXXX")" ||
  fail PRIVATE_TEMP_CREATE_FAILED
chmod 700 -- "$staging"
nonce=''
temp=''
created=0
cleanup() {
  unset pass || true
  if [[ "$created" == 1 ]]; then
    actual="$(docker inspect -f '{{ index .Config.Labels "com.tradeops.usb_recovery_nonce" }}' "$temp" 2>/dev/null || true)"
    if [[ "$actual" == "$nonce" ]]; then
      docker rm -f "$temp" >/dev/null 2>&1 || true
    fi
  fi
  rm -f -- \
    "$staging/tradeops_evidence.dump" \
    "$staging/restore-known-evidence-roles.pg17.sql" \
    "$staging/SHA256SUMS"
  rmdir -- "$staging" 2>/dev/null || true
}
trap cleanup EXIT

printf 'Enter the GPG recovery passphrase locally (not into chat): ' >&2
IFS= read -r -s pass </dev/tty || fail PASSPHRASE_READ_FAILED
printf '\n' >&2
# Decrypt actual USB copy into a temporary WSL folder, never to USB.
gpg --batch --yes --no-symkey-cache --pinentry-mode loopback \
  --passphrase-fd 3 --decrypt --output - "$cipher" 3<<<"$pass" |
  tar -xf - --no-same-owner --no-same-permissions -C "$staging" ||
  fail USB_GPG_DECRYPT_FAILED
unset pass
for name in tradeops_evidence.dump restore-known-evidence-roles.pg17.sql SHA256SUMS; do
  [[ -f "$staging/$name" && ! -L "$staging/$name" ]] || fail DECRYPTED_FILE_MISSING
done
(
  cd -- "$staging"
  sha256sum --check --status -- SHA256SUMS
) || fail DECRYPTED_PLAINTEXT_SHA256_MISMATCH

nonce="$(od -An -N6 -tx1 /dev/urandom | tr -d ' \n')"
[[ "$nonce" =~ ^[a-f0-9]{12}$ ]] || fail NONCE_FAILED
temp="tradeops-usb-offdisk-pg17-$nonce"
docker inspect "$temp" >/dev/null 2>&1 && fail TEMP_CONTAINER_COLLISION

docker run --detach --rm --name "$temp" \
  --network none --tmpfs /var/lib/postgresql/data:rw,size=512m \
  --env POSTGRES_HOST_AUTH_METHOD=trust \
  --env POSTGRES_DB=postgres \
  --label "com.tradeops.usb_recovery_nonce=$nonce" \
  postgres:17-bookworm >/dev/null || fail TEMP_PG17_START_FAILED
created=1
ready=0
for _ in $(seq 1 45); do
  if docker exec -u postgres "$temp" pg_isready -q \
    -U postgres -d postgres >/dev/null 2>&1; then
    ready=1; break
  fi
  sleep 2
done
[[ "$ready" == 1 ]] || fail TEMP_PG17_READY_TIMEOUT

docker exec -i -u postgres "$temp" psql -X -q -v ON_ERROR_STOP=1 \
  -U postgres -d postgres < "$staging/restore-known-evidence-roles.pg17.sql" >/dev/null ||
  fail RECOVER_ROLE_BOOTSTRAP_FAILED
docker exec -i -u postgres "$temp" pg_restore --exit-on-error \
  --create --username=postgres --dbname=postgres \
  < "$staging/tradeops_evidence.dump" >/dev/null ||
  fail RESTORE_FROM_USB_GPG_FAILED

audit="$(docker exec -u postgres "$temp" psql -X -q -A -t \
  -v ON_ERROR_STOP=1 -U postgres -d tradeops_evidence -c "
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
  (SELECT count(*) FROM broker_evidence.account_bindings)::text || '|' ||
  (SELECT count(*) FROM broker_evidence.position_snapshots)::text || '|' ||
  (SELECT count(*) FROM broker_evidence.position_observations)::text || '|' ||
  (SELECT count(*) FROM broker_evidence.execution_observations)::text || '|' ||
  (SELECT count(*) FROM broker_evidence.trade_query_runs)::text || '|' ||
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
")" || fail RESTORED_PG17_AUDIT_FAILED
expected='tradeops_evidence|17|5|1|5|1|1|0|0|0|0|0|1|0|1|0|1|0|1|1|0|0|0|0|0'
[[ "$audit" == "$expected" ]] || fail RESTORED_ROLES_OR_ACLS_INCORRECT

[[ "$(docker inspect -f '{{ index .Config.Labels "com.tradeops.usb_recovery_nonce" }}' "$temp")" == "$nonce" ]] ||
  fail TEMP_PG17_NONCE_MISMATCH
docker rm -f "$temp" >/dev/null 2>&1 || fail TEMP_PG17_CLEANUP_FAILED
created=0
printf 'TRADEOPS_EXTERNAL_USB_RECOVERY_PASS encrypted_usb_checksum=PASS local_passphrase_decrypt=PASS plaintext_checksum=PASS isolated_pg17_restore=PASS tables=5 roles=NOLOGIN grants=RESTORED denies=ENFORCED source_pg_writes=0 deribit_calls=0\n'
