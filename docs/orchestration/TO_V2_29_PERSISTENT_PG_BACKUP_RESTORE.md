# TO-V2-29 — Persistent PostgreSQL 17 pre-migration backup and isolated restore

**Stacked base:** TO-V2-28 Draft PR #30, owner-bootstrap-passed SHA
`f89ba6cca091d0514c0b8c77cf7eaed64419791c`.

## Owner-confirmed starting state

The owner executed the immutable TO-V2-28 bootstrap script on Windows/WSL2
and supplied the following aggregate marker:

```text
TRADEOPS_PG17_EMPTY_DEPLOY_PASS database=tradeops_evidence major=17 schema_present=0 persistent_volume_verified=yes status=healthy broker_calls=0 migration_calls=0
```

Consequently the separate `tradeops-deribit-evidence-pg17` PostgreSQL 17
container and `tradeops-deribit-evidence-pg17-data` Docker volume are
owner-confirmed. No broker-evidence schema or real Deribit records have been
imported. This evidence qualifies **only the bootstrap at the pinned SHA**;
it is NOT a qualified full .NET Gate A for PR #30.

## TO-V2-29 implementation and limits

- `deploy/deribit-evidence/backup-verify-empty-pg17.sh`: inspects only the
  explicitly named TradeOps evidence container. Requires healthy state,
  project component label, the exact mounted persistent volume, PostgreSQL
  major 17, database name and **absent** `broker_evidence` schema.
- Performs a PostgreSQL custom-format `pg_dump` of this empty/pre-migration
  database. It writes a private 0600 archive and SHA-256 checksum OUTSIDE
  Docker volumes, in owner WSL's
  `~/.local/share/tradeops/deribit-evidence-pg17/backups/`. It never
  prompts for or exports the existing PostgreSQL bootstrap password.
- Validates the archive with `pg_restore --list`, then executes a **real**
  `pg_restore --exit-on-error` in a new PostgreSQL 17 container with
  `--network none`, no published port, separate 512 MiB temporary PGDATA
  filesystem and no source volume mount. The restore container is
  destroyed ONLY when its per-run random nonce label matches. No
  existing TradeOps/JobRadar/external-agent container is modified.
- Compares restored database identity, PostgreSQL major and absent evidence
  schema; keeps the archive/checksum only after successful restore and
  verified cleanup.
- `scripts/deribit/Verify-EmptyEvidencePg17Backup.ps1`: PowerShell 5.1
  owner-local launcher. Fetches and strictly pins an exact GitHub HEAD,
  retrieves the exact Bash blob, uses slash-safe `wslpath -u -a`, checks
  staged file exists and passes `bash -n` before executing.
- `tests/TradeOps.UnitTests/DeribitPg17BackupRestoreScriptTests.cs`:
  offline guards for the exact-SHA launcher and isolation of the restore.

**Allowed scope:** creating a backup file and writing solely inside
the fresh disposable, unnetworked restore-check container. The persistent
PostgreSQL database is **read-only** for this script. No migration, role
change, account binding, Deribit API request, trade modification, or real
data import is authorized by this workstream.

## Operational acceptance markers

```text
TRADEOPS_PG17_BACKUP_RESTORE_PASS source=tradeops_evidence major=17 schema_present=0 isolated_restore=PASS backup_saved=yes backup_sha256=yes persistent_writes=0 broker_calls=0
TRADEOPS_PG17_BACKUP_FILE=<private WSL path>
```

These are expectations; owner must run pinned script and provide actual
sanitized output. Source `pg_dump` data, archive contents, SQL records,
passwords and service environment variables must **not** be posted in chat
or GitHub.

### Backup reliability caveat

This archive is **outside the Docker data volume**, but it is still in
the same WSL virtual disk and therefore may share the same host failure
domain. Before importing actual broker financial evidence, require an
**encrypted off-WSL/off-host copy** with independent retention and a
separately verified restore. A single empty-database restore proves this
bootstrap's procedure, NOT future recoverability of a populated exchange
ledger, point-in-time recovery, WAL archival or disaster recovery.

For actual broker data create a separate reviewed backup/restore and
retention workstream; this script fails closed once `broker_evidence` exists.

### Separate future approvals

1. Validate this exact new PR HEAD through independent .NET 10 Release
   restore/build/xUnit with no failed or skipped tests, plus Bash syntax
   and cleanup gates.
2. Obtain owner result for one isolated backup+restore run.
3. Review security of schema ownership, independent backup retention,
   provision read-only broker credential/account binding, then request
   **specific owner consent** before applying any persistent-schema SQL.
4. Only after migration and integrity checks, separately authorize
   one-shot owner-local Deribit read-only position capture, still with
   no exchange trading mutations.

Do not run `docker compose down -v`, `docker volume rm`, `prune`,
legacy `TradeOpsDbContext` migrations or connect to other projects'
database instances.
