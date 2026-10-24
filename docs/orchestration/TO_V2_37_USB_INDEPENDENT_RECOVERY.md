# TO-V2-37 — independent USB encrypted recovery and PG17 restore

## Known evidence and limits

- Owner confirmed full PG17 schema, two NOLOGIN roles, owner/GRANT/deny
  restore in isolated PostgreSQL 17 at Draft PR #37, immutable
  c726b501fae4b49e2c66789567cdaa4232103d50.
- Owner confirmed portable AES256 GPG symmetric encryption, actual
  decryption and SHA256 equality of the post-migration recovery
  bundle at Draft PR #38, immutable
  8cbb0ab74c8dfdde664dfa9b2243c4d30ab24498.
- GPG encrypted source file is under D:\TradeOps-Backups with
  basename tradeops-pg17-recovery-20261024T102605Z-54389110da99.tar.gpg,
  accompanied by a .sha256 checksum file.
- Only ONE physical 512 GB NVMe was present during the owner's previous
  Windows Get-Disk report, with C:, D: and E: on that same NVMe.
  The D: copy is off-WSL but NOT a separate failure domain.

**There is no evidence yet that another USB disk exists. Do not run this
procedure until a separate external USB mass-storage device has been
attached and an NTFS or exFAT letter has been assigned.**

## New two-part owner operation

1. scripts/deribit/Copy-AndVerifyEvidenceRecoveryUsb.ps1 accepts the
   reviewed Git HEAD and explicit USB drive letter. It refuses C:, D:,
   E: and any drive on the same physical disk as source D:. It also
   requires the destination Get-Disk BusType to be USB, not boot/system/
   read-only/offline, filesystem NTFS or exFAT, and adequate space.
   It checks the exact source .gpg and .sha256 checksum before writing.
2. The Windows script loads the exact pinned Bash verifier from Git,
   validates its Bash syntax and WSL path, then adds the ciphertext and
   SHA256 file ONLY to the selected USB's TradeOps-Backups folder.
   It uses unique partial filenames during copy and refuses to replace
   preexisting backups. SHA256 and sidecar equality are checked before
   finalization. The original D: and WSL copies are untouched.
3. deploy/deribit-evidence/verify-independent-usb-recovery-pg17.sh
   reads the encrypted file DIRECTLY from that newly created USB copy,
   verifies the external SHA256, and prompts the owner for their
   existing GPG recovery passphrase directly on /dev/tty without echo.
   Neither GitHub nor PowerShell stores a password or private key.
4. The Bash script decrypts the USB ciphertext into a private,
   disposable WSL directory; checks all plaintext SHA256SUMS.
   It starts a fresh postgres:17-bookworm Docker container with no
   network, no published port and tmpfs data; bootstraps the two
   NOLOGIN roles from the decrypted bundle, pg_restores the dump with
   original ownership and ACLs, and verifies all five empty evidence
   tables, two NOLOGIN roles, schema/table ownership, and positive/
   denied PostgreSQL privileges. The temporary container is deleted
   only if its per-run label matches its nonce.
5. It prints a success marker ONLY after the independent copy
   survives decryption and actual PG17 restore. No permanent
   PostgreSQL SQL writes or Deribit API calls are made.

Expected marker (NOT YET OWNER VERIFIED):
TRADEOPS_EXTERNAL_USB_RECOVERY_PASS encrypted_usb_checksum=PASS local_passphrase_decrypt=PASS plaintext_checksum=PASS isolated_pg17_restore=PASS tables=5 roles=NOLOGIN grants=RESTORED denies=ENFORCED source_pg_writes=0 deribit_calls=0

## Boundaries and remaining work

- Requires a dedicated, physically separate USB device. It does NOT
  automatically send any file to a cloud storage provider. Alternative
  remote encrypted backup is a different workstream and requires
  user-approved destination, privacy and access policy.
- This USB rehearsal should only be run against a trusted physical
  device selected by the owner. It does not overwrite/delete existing
  media contents and it does not format the device.
- The backup includes only the empty, pre-data evidence schema and
  two NOLOGIN roles. The GPG passphrase must be stored securely OFF
  the primary NVMe; without it the encrypted copy cannot be restored.
- Even after the backup exists on a separate USB, reliability requires
  an ongoing retention and rotation process. Offsite placement protects
  against theft/fire at the primary location. A single USB is not
  equivalent to multiple independent retained backups.
- Before ANY real read-only Deribit position import, establish a
  versioned recurring encrypted **populated** database backup policy,
  account binding controls, a least-privilege LOGIN credential flow,
  and independent .NET/runtime tests.
- No Deribit trading requests, public Orders/Fills mutations, other
  PostgreSQL containers, real financial data or API/Worker wiring.
- New-SHA .NET Gate A and owner actual USB + PG17 restore: NOT YET RUN.
- Keep Draft/unmerged. Freeze all owner-qualified predecessor SHAs.
