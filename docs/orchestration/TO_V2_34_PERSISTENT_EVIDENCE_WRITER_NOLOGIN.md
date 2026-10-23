# TO-V2-34 — owner-gated NOLOGIN evidence writer installation

## Frozen, owner-observed PG17 acceptance evidence

- PR #30 / TO-V2-28 exact SHA f89ba6cca091d0514c0b8c77cf7eaed64419791c:
  independent empty persistent PG17 bootstrap PASS.
- PR #31 / TO-V2-29 exact SHA c48fac172133014639d49c5909f3cf867a6138b6:
  pre-migration backup and actual disposable PG17 restore PASS.
- PR #32 / TO-V2-30 exact SHA a1b4d5e616d1568cbd22924a4117ffcc143625d7:
  five-table broker evidence SQL rehearsal PASS.
- PR #33 / TO-V2-31 exact SHA b64885f9cef2eb66fff09292ca23df20d34bae20:
  owner-authorized permanent schema creation PASS, NOLOGIN table owner.
- PR #34 / TO-V2-32 exact SHA dace7298442a94884c2820320e909643d642ee5e:
  post-schema pg_dump and full network-isolated PG17 restore PASS.
- PR #35 / TO-V2-33 exact SHA 8e4e53fa86e5bd81fe2a735e223956200c1e96cf:
  owner-run isolated real PostgreSQL writer role permission rehearsal PASS;
  source permanent role remains absent, and no actual Deribit request occurred.

These are owner-reported aggregate markers. Prior heads are qualified only
for their stated owner-run gate, not independent .NET CI, and remain Draft.

## TO-V2-34 candidate (new, NOT YET AUTHORIZED FOR PERSISTENT DDL)

The previous SQL proposal deploy/deribit-evidence/
TO_V2_33_WRITER_ROLE_PROPOSED.sql was successfully exercised in a
disposable PostgreSQL 17 against synthetic fixtures. It creates
tradeops_evidence_writer with NOLOGIN, NOSUPERUSER, NOCREATEDB,
NOCREATEROLE, NOREPLICATION and limits connections to 2.

The resulting role may CONNECT to tradeops_evidence, USAGE
broker_evidence schema, SELECT account_bindings, and INSERT ONLY
position_snapshots and position_observations. It may not mutate
account_bindings, issue DDL, UPDATE/DELETE position observations, or
INSERT execution_observations and trade_query_runs.

Even when installed, a NOLOGIN role has NO usable database credentials
and cannot connect independently. Actual credentials, enabling LOGIN,
Deribit account binding and first authenticated write require separate
approval, security review and owner-local provisioning.

## New owner-only migration scripts

- deploy/deribit-evidence/install-writer-nologin.owner-gated.pg17.sh
- scripts/deribit/Install-PersistentEvidenceWriterNoLogin.ps1
- tests/TradeOps.UnitTests/DeribitPersistentEvidenceWriterInstallScriptTests.cs

The exact-SHA PowerShell wrapper refuses any branch movement and
requires owner parameters ApprovedDatabase tradeops_evidence and
ApproveNoLoginWriterInstallation. Only after owner confirmation may the
wrapper stage the reviewed Bash+SQL blobs and run the migration.

Bash refuses wrong container health/project/component/volume/database,
PostgreSQL major !=17, missing or non-owned schema, existing writer role,
nonempty broker_evidence tables or missing/mismatched backup checksum.
The prior post-schema backup was really restored on the owner's
independent no-network ephemeral PG17.

Only the pinned one-transaction SQL proposal executes on the persistent
database. SQL guards validate account identity/role absence, create the
NOLOGIN role, grant the minimal rights and verify denied privileges.
The wrapper independently audits NOLOGIN, positive INSERT/SELECT,
denied UPDATE/DELETE/account/execution INSERT and zero broker rows
after commit. It prints aggregate-only markers with no account IDs or
DB password. Failure MUST be examined with read-only introspection,
not blind retry or DROP ROLE.

Owner target for separate approval:
  Container: tradeops-deribit-evidence-pg17
  Database: tradeops_evidence (PostgreSQL 17)
  Role: tradeops_evidence_writer
  Status after install: NOLOGIN and no password
  Write scope: exactly zero account/position/execution/trade-query rows
  Legacy live Orders/Fills, trading APIs, API/Worker DI: unchanged

The repo-root docker-compose.yml and other project PostgreSQL
containers are never touched. No script contains or requests the
owner's Deribit client ID/secret or PostgreSQL passwords.

## Expected marker (not evidence until actual owner run)

TRADEOPS_PG17_WRITER_INSTALL_PASS database=tradeops_evidence major=17 role=tradeops_evidence_writer login=DISABLED binding_select=YES position_insert=YES update_delete=DENIED execution_insert=DENIED role_count=1 broker_rows=0 deribit_calls=0

## Unresolved before any real financial evidence import

- Independent off-WSL/off-host encrypted copy of a current backup,
  key custody and recovery test. Previous backups are on the same
  WSL virtual disk; SHA256 is not encryption.
- Owner-authorized private PostgreSQL LOGIN/secret provisioning without
  cleartext scripts or GitHub logs.
- Verified exact Deribit mainnet read-only credential/account binding
  and rotation/immutability policy.
- Full independent .NET 10 Gate A for new code, including regression
  tests, and PG acceptance for the real importer.
- Actual read-only position capture; typed historical executions and
  order evidence import remain separate unimplemented work.

**Current status: candidate Draft only; persistent role NOT RUN.
No new broker APIs, no exchange mutations, no financial rows.**
