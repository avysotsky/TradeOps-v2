# TO-V2-30 — controlled broker_evidence migration rehearsal, NO persistent writes

**Stacked on:** TO-V2-29 Draft PR #31, exact owner-qualified backup/restore
SHA c48fac172133014639d49c5909f3cf867a6138b6.

## Starting point verified by owner (2026-10-23)

Dedicated PostgreSQL 17 container tradeops-deribit-evidence-pg17 is healthy.
Persistent DB tradeops_evidence uses named volume
tradeops-deribit-evidence-pg17-data. The source has NO broker_evidence
schema. Owner ran one custom-format pre-migration backup and a real independent
restore in a network-disabled PostgreSQL 17 disposable container and supplied:

TRADEOPS_PG17_BACKUP_RESTORE_PASS source=tradeops_evidence major=17 schema_present=0 isolated_restore=PASS backup_saved=yes backup_sha256=yes persistent_writes=0 broker_calls=0

The verified archive remains on WSL2 storage. It has NOT been independently
copied to encrypted external/off-host storage; a common-mode host failure
would still threaten both DB and archive. Do not overstate that risk.

## The new rehearsal

- Reads only the exact named persistent PostgreSQL container (health,
  project label, data-volume identity, PG17 major, DB and absent schema).
- Re-verifies the locally retained pre-migration SHA-256 checksum.
- Starts a **new temporary** PostgreSQL 17 with no network, no port, no
  persistent mounts and a tmpfs-only PGDATA.
- Replays the proposal
  scripts/deribit/sql/TO_V2_28_BROKER_EVIDENCE_SCHEMA_PROPOSED.sql
  into that isolated database ONLY.
- Checks expected five broker_evidence tables.
- Runs synthetic SQL fixture for two distinct account bindings sharing
  one execution ID, refusal of same-account ID re-use with changed fields,
  preservation of signed native position size and original fee. The fixture
  runs inside a transaction which is rolled back.
- Re-reads the persistent source to prove broker_evidence is still absent;
  removes only the temporary container after verifying a random nonce label.
- Produces aggregate-only PASS/FAIL markers; does not output account
  fingerprints, DB passwords, credentials, external IDs or trade rows.

Exact-SHA PowerShell 5.1 operator wrapper:
scripts/deribit/Rehearse-BrokerEvidenceSchemaPg17.ps1

Bash process:
deploy/deribit-evidence/rehearse-evidence-schema.pg17.sh

SQL synthetic fixture:
deploy/deribit-evidence/verify-schema-invariants.synthetic.sql

All paths are fetched from the pinned Git commit. Never use a mutable working
tree file without checking its SHA. The Windows wrapper checks WSL paths
using forward slashes and checks Bash syntax before execution.

## Hard gate after rehearsal

Passing TO-V2-30 does NOT authorize migration or account binding.
For the requested **real persistent** schema, a separate explicit approval
is required naming database **tradeops_evidence** in container
**tradeops-deribit-evidence-pg17**, destination schema **broker_evidence**,
with previously reviewed exact SQL SHA, dedicated schema owner, tested
rollback/backup, and private non-superuser evidence writer credentials.
There must also be a verified independent encrypted backup/recovery path
outside the WSL virtual disk before importing actual Deribit financial data.

Do not change legacy TradeOps Orders, Fills, or any Worker/API DI registration.
Do not point the synthetic journal at persistent data.
Do not enable Deribit order placement, cancellation or transfer.
Do not mistake the preparatory execution_observations SQL for an
implemented typed history importer.

## Qualification status

- Source PG17 bootstrap owner PASS at TO-V2-28 SHA f89ba6cca091d0514c0b8c77cf7eaed64419791c.
- Empty source backup/restore owner PASS at TO-V2-29 SHA c48fac172133014639d49c5909f3cf867a6138b6.
- TO-V2-30 independent .NET 10 Gate A: NOT YET RUN.
- TO-V2-30 owner isolated schema rehearsal: NOT YET RUN.
- Persistent broker_evidence migration: NOT AUTHORIZED / NOT RUN.
- Real Deribit account snapshots persisted: NOT YET RUN.
- Real executions persisted: NOT IMPLEMENTED.
