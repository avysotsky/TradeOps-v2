# TO-V2-31 — persistent broker_evidence schema (owner gated)

## Pinned upstream evidence (2026-10-23)

1. TO-V2-28 PR #30 at SHA f89ba6cca091d0514c0b8c77cf7eaed64419791c:
   independent empty PostgreSQL 17 deployment owner PASS; data volume
   tradeops-deribit-evidence-pg17-data, container
   tradeops-deribit-evidence-pg17, database tradeops_evidence.
2. TO-V2-29 PR #31 at SHA c48fac172133014639d49c5909f3cf867a6138b6:
   PostgreSQL custom-format pre-migration dump and actual restore in separate
   network-disabled PG17 container owner PASS. Backup and SHA256 under the
   private WSL user profile; no persistent DB writes.
3. TO-V2-30 PR #32 at SHA a1b4d5e616d1568cbd22924a4117ffcc143625d7:
   proposed DDL applied ONLY to a network-disabled ephemeral PG17; five
   broker_evidence tables verified; account-scoped execution uniqueness and
   signed native position sizes verified; persisted DB schema still absent.
   Owner PASS.

## This workstream: proposal, NOT yet approved/applied

- New Bash migration script
  deploy/deribit-evidence/apply-evidence-schema.owner-gated.pg17.sh.
  It refuses to run unless the owner supplies an explicit approval token
  for this database and the exact pre-reviewed schema SQL file.
- Windows wrapper
  scripts/deribit/Apply-PersistentBrokerEvidenceSchemaPg17.ps1
  requires both -ApprovedDatabase tradeops_evidence and -ApproveMigration.
  It fetches a specific branch, pins the remote Git SHA, stages only the
  two immutable SQL/Bash objects and runs a WSL Bash syntax check before
  invoking.
- Verifies source container health, TradeOps labels, persistent volume,
  PostgreSQL major 17, exact database, absent broker_evidence schema and
  absent preexisting schema-owner role. Revalidates the pre-migration WSL
  backup SHA256. Does not query foreign project databases.
- Executes one transactional migration using the exact existing
  TO_V2_28_BROKER_EVIDENCE_SCHEMA_PROPOSED.sql content.
  A separate NOLOGIN/NOSUPERUSER schema-owner role
  tradeops_evidence_owner is created; temporary CREATE permission is
  granted on only this database and revoked before commit.
  All five tables and their indexes are created while SET ROLE
  tradeops_evidence_owner is active. The migration runs as local PostgreSQL
  administrator only for DDL; it has no Deribit credential access.
  Any psql error causes an uncommitted transaction to roll back, including
  role creation.
- Post-commit aggregate verification: exactly five tables owned by
  tradeops_evidence_owner and 0 account binding, position snapshot,
  execution observation and query-run rows.
- The previously drafted writer role provisioning SQL is NOT invoked here.
  No SQL binds any real trading account, no DERIBIT secret is read and
  no API/Worker registration or trade-history importer is enabled.

## Approval to request separately

Approval must clearly identify:
- Container: tradeops-deribit-evidence-pg17
- Database: tradeops_evidence, PostgreSQL 17
- Additive schema: broker_evidence (five tables, indexes)
- Schema owner: tradeops_evidence_owner, NOLOGIN
- Scope: schema DDL only; **no Deribit request, credential binding, rows
  import, live trading, API/Worker wiring or changes to Orders/Fills**
- An exact SHA of THIS PR, not an earlier successful rehearsal SHA

Do not execute the wrapper or its embedded approval token merely because
this PR exists. Wait for owner confirmation. Once the owner approves, invoke
the exact pinned Git object and inspect the output. If a failure happens,
DO NOT blindly retry or drop anything: inspect non-secret state to
distinguish rollback from already committed migration.

## Backup and restoration limits

The pre-migration backup was successfully restored but remains on the
same WSL virtual disk. It is not independent off-host recovery. Before
persisting actual real Deribit position/trade records, establish an
encrypted independently retained backup and test recovery using its
restoration key. Preserve the original pre-migration dump and do not
use destructive rollback on already populated broker_evidence tables.

## Qualification status

- This PR's new-SHA .NET Gate A: not yet observed.
- Persistent schema migration: **NOT YET OWNER APPROVED / NOT RUN**.
- Dedicated writer role and account binding: NOT PROVISIONED.
- Real authenticated Deribit persistence: NOT RUN.
- Legacy Orders/Fills and broker mutations: not touched.
