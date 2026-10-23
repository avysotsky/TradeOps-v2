# TO-V2-33 — evidence writer role, isolated PostgreSQL 17 rehearsal

## Qualified owner-local chain at 2026-10-23

- PR #30 exact SHA f89ba6cca091d0514c0b8c77cf7eaed64419791c:
  independent PostgreSQL 17 empty persistent deployment PASS.
- PR #31 exact SHA c48fac172133014639d49c5909f3cf867a6138b6:
  pre-migration custom backup and actual isolated PostgreSQL 17 restore PASS.
- PR #32 exact SHA a1b4d5e616d1568cbd22924a4117ffcc143625d7:
  schema proposal in disposable PG17, account-scoped identity checks PASS.
- PR #33 exact SHA b64885f9cef2eb66fff09292ca23df20d34bae20:
  owner-authorized persistent schema migration PASS, five tables owned by
  tradeops_evidence_owner (NOLOGIN) with all broker records empty.
- PR #34 exact SHA dace7298442a94884c2820320e909643d642ee5e:
  post-schema custom archive, checksum and real restore into a separate
  network-disabled PostgreSQL 17 PASS; zero persistent writes.

All predecessor Draft PRs/qualified commits must stay frozen. These are
OWNER-REPORTED results, not independent .NET 10 Gate A verification.

## Proposed role, not yet provisioned

deploy/deribit-evidence/TO_V2_33_WRITER_ROLE_PROPOSED.sql is a standalone
transactional PostgreSQL 17 proposal. It refuses an unexpected database,
missing broker_evidence, wrong schema ownership, existing writer role or any
broker records before starting. It creates:

  tradeops_evidence_writer (NOLOGIN, NOSUPERUSER, NOCREATEDB,
  NOCREATEROLE, NOREPLICATION, max 2 connections)

The restricted role is allowed:
- CONNECT to database tradeops_evidence;
- USAGE on broker_evidence schema;
- SELECT on account_bindings so the existing real snapshot writer can
  verify its owner-approved credential binding;
- INSERT into position_snapshots and position_observations, and nothing more.

The script verifies it cannot CREATE in the database, public schema or
broker_evidence, cannot SELECT/UPDATE/DELETE position_snapshots, cannot
INSERT account_bindings, executions or trade_query_runs. No PUBLIC grants
are changed and no privileged role can log in using this writer because
it is deliberately NOLOGIN. Role session timeouts and pg_catalog-only
search path are configured as defense in depth.

Creating this role in the persistent database will require NEW explicit
owner approval and a separate owner-only migration script. **THIS PR
DOES NOT INSTALL THE ROLE INTO PERSISTENT POSTGRESQL.**

It does not create or infer a Deribit account ID, does not read DPAPI
broker credentials, does not read wallet data, and does not connect to
Deribit. No account binding, real position capture, order placement,
cancel or transfer is performed.

## Owner-run rehearsal

- deploy/deribit-evidence/rehearse-writer-role.pg17.sh requires a healthy
  exact TradeOps PostgreSQL 17 container with expected volume, schema,
  NO already-installed writer role and zero broker evidence records.
  It checks the private WSL post-schema backup archive SHA256.
- Starts a NEW disposable postgres:17-bookworm with --network none,
  no published port, no persistent mounts and tmpfs PGDATA.
- Applies pinned schema SQL ONLY there, recreates synthetic NOLOGIN
  schema ownership, applies the proposed role/GRANT SQL, then runs
  TO_V2_33_WRITER_PERMISSIONS_SYNTHETIC.sql.
- The synthetic SQL uses SET ROLE to exercise real PostgreSQL permission
  enforcement. SELECT bindings and INSERT position evidence succeed;
  UPDATE, DELETE, CREATE TABLE, binding INSERT and execution INSERT must
  fail as insufficient_privilege. It then RESET ROLE and ROLLBACK.
- Checks the lab role is NOLOGIN and that no fixture records remain,
  then confirms the persistent source still has no writer or records.
  Cleanup removes only the per-run nonce-labeled disposable container.
- scripts/deribit/Rehearse-EvidenceWriterRolePg17.ps1 pins branch HEAD
  to owner-selected exact SHA and stages immutable SQL/Bash using Git,
  checks Windows-to-WSL paths and Bash syntax before executing.

Expected marker ONLY, not yet executed:

TRADEOPS_PG17_WRITER_ROLE_REHEARSAL_PASS source=tradeops_evidence major=17 writer_login=DISABLED account_binding_select=PASS positions_insert=PASS update_delete_ddl=DENIED executions_insert=DENIED lab_rollback=PASS persistent_roles_created=0 persistent_writes=0 deribit_calls=0

## Other mandatory gates before actual financial data import

A validated post-schema backup is still held on the same WSL virtual
disk; independent encrypted external/off-WSL retention and a tested
restore are required before storing actual Deribit financial records.
After the rehearsal, separately approve creating the NOLOGIN role
in persistent DB. Only later, independently approve a secure owner-local
LOGIN/password provisioning flow, binding a concrete read-only Deribit
mainnet credential to an attested account and first one-shot position
capture. The actual Deribit credential and any DB password must never
appear in chat, GitHub, PowerShell command history or logs.

Execution/trade history ingestion and typed replay are NOT implemented
by this role; only future position snapshots are eligible for INSERT.

New SHA independent .NET build/xUnit Gate A: NOT YET VERIFIED.
Owner's real role rehearsal Gate B: NOT YET RUN.
Persistent role deployment and real Deribit capture: NOT AUTHORIZED.
Keep PR Draft/unmerged.
