# TO-V2-36 — Portable encrypted off-WSL recovery copy (Windows D:)

## Grounded owner diagnostics, 2026-10-24

Owner verified:
- WSL bundle SHA256: PASS for the full schema/role/ACL recovery artifact:
  /home/user/.local/share/tradeops/deribit-evidence-pg17/backups/role-acl-recovery-20261024T100019Z-8DoC6C
- GPG: AVAILABLE. tar and sha256sum: AVAILABLE. age: MISSING.
- Windows Get-Disk: exactly ONE 512 GB NVMe physical disk.
- C:, D: and E: are partitions/volumes on that same NVMe.
- Approximately 17.4 GB available on D: when measured.

This means copying to D: **is off-WSL but NOT an independent
physical or off-host backup**. Failure of that NVMe may destroy the
source DB, WSL original bundle and encrypted D: export together.

The pre-data backup is known to have five broker_evidence tables, two
NOLOGIN roles, owners and restricted GRANTs, with zero real Deribit
broker rows. Owner ran a complete independent PostgreSQL 17 restore,
source read-only, at Draft PR #37 SHA
c726b501fae4b49e2c66789567cdaa4232103d50.

## Changes (new owner-local encryption only)

- deploy/deribit-evidence/export-gpg-recovery-to-windows-d.sh
- scripts/deribit/Export-Pg17RecoveryGpgWindowsD.ps1
- tests/TradeOps.UnitTests/DeribitGpgPortableRecoveryExportTests.cs

The exact-SHA Windows wrapper fetches only a pinned Git object
from a specified reviewed branch, validates its WSL path and Bash syntax,
then starts the WSL script as existing local user `user`.
No PowerShell secrets, DB credentials, broker calls or Docker operations.

The Bash script:
1. Revalidates the source bundle's original SHA256SUMS, three expected
   regular files and owner/private local backup directory.
2. Ensures /mnt/d is genuinely mounted and that the output path
   /mnt/d/TradeOps-Backups is not a symlink; refuses inadequate space.
3. Requires an interactive /dev/tty and asks the owner to enter a
   NEW recovery passphrase twice, without shell echo. It refuses
   passphrases shorter than 24 bytes. Never pass passphrases in
   shell arguments, process list, files, GitHub or chat; store them
   independently off this computer.
4. Streams exactly those three files as tar from WSL over stdin
   through GnuPG symmetric AES-256 encryption using salted iterated
   S2K SHA-256 with a high work factor. FD3 supplies the passphrase
   to GPG in loopback mode; no --passphrase argument or file. The
   Windows D volume receives ONLY ciphertext, not a plaintext tar.
5. Decrypts the Windows D ciphertext while still in a temporary
   filename, extracts into a private ephemeral WSL directory,
   checks original SHA256SUMS and bytewise cmp against every source
   file. The intermediate decrypted files and secret shell variables
   are cleared on exit.
6. Moves a verified encrypted artifact to its unique D path,
   creates and verifies its external SHA256 checksum. Never overwrites
   a previous backup. No persistent PostgreSQL writes.

GnuPG CLI reference:
https://www.gnupg.org/documentation/manuals/gnupg/GPG-Esoteric-Options.html
- --pinentry-mode loopback and --batch required with --passphrase-fd
- --no-symkey-cache disables symmetric passphrase cache
- --s2k-mode 3, --s2k-digest-algo SHA256 and --s2k-count set salted
  iterated password-based key derivation.
GnuPG is portable between WSL and other modern Linux/Windows systems.
The user MUST retain the correct passphrase; SHA256 does not recover it.

## Expected owner marker (NOT YET OBSERVED)

TRADEOPS_GPG_RECOVERY_EXPORT_PASS encryption=AES256 s2k=iterated-salted-sha256 destination=WINDOWS_D encrypted_roundtrip=PASS plaintext_sha256=PASS ciphertext_sha256=PASS independent_physical_disk=NO original_bundle_preserved=YES deribit_calls=0 persistent_db_writes=0

The output also contains a path to an encrypted .tar.gpg file and its
external .sha256 checksum; neither plaintext nor passphrase is printed.

## Critical remaining disaster-recovery gate

This step alone cannot defend against loss of the one physical NVMe
or theft of both disk and local encryption passphrase if stored there.
Owner must later place the encrypted GPG artifact AND checksum on
another physically independent USB/SSD or a suitable private remote
backup target, verify their SHA256, and perform a genuine off-host
decrypt and PostgreSQL restore test with separately kept passphrase.
Do not claim independent disaster recovery readiness on a D: copy.

After real Deribit position imports start, this pre-data-only recovery
artifact is insufficient; schedule fresh, retained, encrypted full
backups and test recovery of populated snapshots, privilege attributes,
and possible new LOGIN role configuration.

This PR does NOT grant credentials to the PostgreSQL NOLOGIN writer,
create account bindings, import market data or execute trades.
No legacy Orders/Fills, other DBs, Docker volumes or API registrations
are touched.

New-SHA .NET Gate A: NOT YET VERIFIED.
Owner encryption/export and decrypt gate: NOT YET RUN.
Keep the PR Draft/unmerged.
