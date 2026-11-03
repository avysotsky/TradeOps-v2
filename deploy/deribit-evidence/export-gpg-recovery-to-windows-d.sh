#!/usr/bin/env bash
# TO-V2-36: encrypt one pinned, verified pre-data recovery bundle outside WSL.
# Destination D: is a different partition, NOT an independent physical disk.
# User enters a passphrase locally on /dev/tty. NEVER log or persist it.
set -Eeuo pipefail
set +x
umask 077

fail() {
  printf 'TRADEOPS_GPG_RECOVERY_EXPORT_FAIL code=%s\n' "$1" >&2
  exit 1
}

[[ $# -eq 0 ]] || fail UNEXPECTED_ARGUMENTS
for cmd in tar gpg sha256sum cmp mktemp mountpoint df du od; do
  command -v "$cmd" >/dev/null 2>&1 || fail REQUIRED_TOOL_MISSING
done
[[ -r /dev/tty && -w /dev/tty ]] || fail INTERACTIVE_TTY_REQUIRED

root="$HOME/.local/share/tradeops/deribit-evidence-pg17"
bundle="$root/backups/role-acl-recovery-20261024T100019Z-8DoC6C"
target='/mnt/d/TradeOps-Backups'
[[ -d "$root" && ! -L "$root" && "$(stat -c '%u' "$root")" == "$(id -u)" ]] ||
  fail PRIVATE_SOURCE_PARENT_UNSAFE
[[ -d "$bundle" && ! -L "$bundle" && "$(stat -c '%u' "$bundle")" == "$(id -u)" ]] ||
  fail SOURCE_RECOVERY_BUNDLE_UNAVAILABLE

for name in tradeops_evidence.dump restore-known-evidence-roles.pg17.sql SHA256SUMS; do
  [[ -f "$bundle/$name" && ! -L "$bundle/$name" ]] || fail UNSAFE_BUNDLE_FILE
done
(
  cd -- "$bundle"
  sha256sum --check --status -- SHA256SUMS
) || fail SOURCE_BUNDLE_INTEGRITY_FAILED

# /mnt/d is an NTFS partition on the SAME NVMe as WSL, not off-host.
[[ -d /mnt/d && ! -L /mnt/d ]] || fail WINDOWS_D_VOLUME_NOT_FOUND
mountpoint -q /mnt/d || fail WINDOWS_D_NOT_MOUNTED
[[ ! -L "$target" ]] || fail TARGET_SYMLINK
mkdir -p -- "$target" || fail TARGET_CREATE_FAILED
[[ -d "$target" ]] || fail TARGET_NOT_DIRECTORY

bundle_kb="$(du -sk -- "$bundle" | cut -f1)"
free_kb="$(df -Pk -- "$target" | awk 'END {print $4}')"
[[ "$bundle_kb" =~ ^[0-9]+$ && "$free_kb" =~ ^[0-9]+$ ]] ||
  fail DISK_SPACE_PARSE_FAILED
(( free_kb > bundle_kb + 102400 )) || fail TARGET_INSUFFICIENT_SPACE

nonce="$(od -An -N6 -tx1 /dev/urandom | tr -d ' \n')"
[[ "$nonce" =~ ^[a-f0-9]{12}$ ]] || fail NONCE_INVALID
final="$target/tradeops-pg17-recovery-$(date -u +%Y%m%dT%H%M%SZ)-$nonce.tar.gpg"
partial="$final.partial"
hashfile="$final.sha256"
[[ ! -e "$final" && ! -e "$partial" && ! -e "$hashfile" ]] ||
  fail EXPORT_NAME_EXISTS

tempcheck="$(mktemp -d "$root/encrypted-copy-verify-XXXXXXXX")" ||
  fail PRIVATE_DECRYPT_DIRECTORY_CREATE_FAILED
chmod 700 -- "$tempcheck"
committed=0
cleanup() {
  # Only our generated files/directories; never modify original bundle or PG.
  unset pass confirm || true
  rm -f -- "$partial"
  if [[ "$committed" != 1 ]]; then
    rm -f -- "$final" "$hashfile"
  fi
  rm -f -- \
    "$tempcheck/tradeops_evidence.dump" \
    "$tempcheck/restore-known-evidence-roles.pg17.sql" \
    "$tempcheck/SHA256SUMS"
  rmdir -- "$tempcheck" 2>/dev/null || true
}
trap cleanup EXIT

printf '\nRecovery encryption: enter a NEW long passphrase (24+ characters).\n' >&2
printf 'It is NOT stored in Git, a file, or the Windows command line.\n' >&2
printf 'IMPORTANT: record it securely OFF this NVMe disk.\n' >&2
printf 'Enter encryption passphrase: ' >&2
IFS= read -r -s pass </dev/tty || fail PASSPHRASE_READ_FAILED
printf '\nConfirm encryption passphrase: ' >&2
IFS= read -r -s confirm </dev/tty || fail PASSPHRASE_CONFIRM_READ_FAILED
printf '\n' >&2
[[ "$(printf '%s' "$pass" | wc -c)" -ge 24 ]] || fail PASSPHRASE_TOO_SHORT
[[ "$pass" == "$confirm" ]] || fail PASSPHRASE_CONFIRM_MISMATCH
unset confirm

# Stream plaintext TAR straight from WSL into GPG. Only the encrypted
# ciphertext lands on Windows D. Passphrase passes through FD 3, not argv.
(
  cd -- "$bundle"
  tar -cf - -- tradeops_evidence.dump restore-known-evidence-roles.pg17.sql SHA256SUMS
) | gpg --batch --yes --no-symkey-cache \
    --pinentry-mode loopback --passphrase-fd 3 \
    --symmetric --cipher-algo AES256 \
    --s2k-mode 3 --s2k-digest-algo SHA256 --s2k-count 65011712 \
    --compress-algo none --output "$partial" 3<<<"$pass" ||
  fail GPG_ENCRYPT_FAILED

[[ -s "$partial" ]] || fail ENCRYPTED_FILE_EMPTY

# Decrypt from Windows D; only private ephemeral WSL storage gets plaintext.
gpg --batch --yes --no-symkey-cache \
    --pinentry-mode loopback --passphrase-fd 3 \
    --decrypt --output - "$partial" 3<<<"$pass" |
  tar -xf - --no-same-owner --no-same-permissions -C "$tempcheck" ||
  fail EXPORTED_CIPHERTEXT_DECRYPT_FAILED
unset pass

(
  cd -- "$tempcheck"
  sha256sum --check --status -- SHA256SUMS
) || fail DECRYPTED_BUNDLE_CHECKSUM_FAILED
for name in tradeops_evidence.dump restore-known-evidence-roles.pg17.sql SHA256SUMS; do
  cmp -s -- "$bundle/$name" "$tempcheck/$name" ||
    fail DECRYPTED_FILE_MISMATCH
done

mv -n -- "$partial" "$final" || fail FINALIZE_ENCRYPTED_FILE_FAILED
[[ ! -e "$partial" && -s "$final" ]] || fail FINALIZE_FILE_MISSING
(
  cd -- "$target"
  sha256sum -- "$(basename "$final")" > "$(basename "$hashfile")"
  sha256sum --check --status -- "$(basename "$hashfile")"
) || fail ENCRYPTED_OUTPUT_CHECKSUM_FAILED

committed=1
printf 'TRADEOPS_GPG_RECOVERY_EXPORT_PASS encryption=AES256 s2k=iterated-salted-sha256 destination=WINDOWS_D encrypted_roundtrip=PASS plaintext_sha256=PASS ciphertext_sha256=PASS independent_physical_disk=NO original_bundle_preserved=YES deribit_calls=0 persistent_db_writes=0\n'
printf 'TRADEOPS_GPG_RECOVERY_EXPORT_FILE=%s\n' "$final"
printf 'TRADEOPS_GPG_RECOVERY_EXPORT_CHECKSUM=%s\n' "$hashfile"
