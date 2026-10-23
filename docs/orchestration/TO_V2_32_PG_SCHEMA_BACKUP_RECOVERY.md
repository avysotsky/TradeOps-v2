# TO-V2-32 — PostgreSQL 17 post-schema backup and independent restore

## Frozen owner acceptance chain

- PR #30 / TO-V2-28 SHA f89ba6cca091d0514c0b8c77cf7eaed64419791c: owner PostgreSQL 17 empty persistent bootstrap PASS.
- PR #31 / TO-V2-29 SHA c48fac172133014639d49c5909f3cf867a6138b6: owner custom-format pre-migration backup plus independent no-network PostgreSQL 17 restore PASS.
- PR #32 / TO-V2-30 SHA a1b4d5e616d1568cbd22924a4117ffcc143625d7: owner disposable SQL rehearsal, five tables, account-scoped execution and signed-position invariants PASS.
- PR #33 / TO-V2-31 SHA b64885f9cef2eb66fff09292ca23df20d34bae20: owner explicitly approved and ran persistent schema migration: five tables owned by NOLOGIN role tradeops_evidence_owner, zero account bindings/positions/executions/query runs, no Deribit requests, PASS.

All predecessor Draft PRs and passing SHAs remain frozen/unmerged. Owner-reported aggregates are NOT proof of independent .NET Gate A.

## New workstream

The pre-migration backup contains no broker_evidence schema. This PR creates an updated custom-format dump while all five persistent tables are still empty, then restores and validates that SQL schema independently.

- deploy/deribit-evidence/backup-verify-schema-pg17.sh reads ONLY the healthy, exact named TradeOps PostgreSQL 17 source, checks project labels, mounted volume, version, database, five tables owned by NOLOGIN schema owner, and all five tables with ZERO rows.
- Takes pg_dump --format=custom --no-owner --no-acl from persistent source, saves private archive OUTSIDE Docker volume in owner WSL profile.
- Verifies archive listing and performs a real pg_restore --exit-on-error into a new PostgreSQL 17 container with --network none, no port, no source mounts and tmpfs-only PGDATA; verifies five restored tables and empty broker records.
- Removes only the newly created nonce-labelled ephemeral container. Re-reads source for integrity. Persists archive with SHA-256 checksum ONLY after restore succeeds.
- scripts/deribit/Verify-PersistentEvidenceSchemaBackup.ps1 fetches exact pinned GitHub HEAD source and runs PowerShell/WSL path + Bash syntax checks prior to execution.
- tests/TradeOps.UnitTests/DeribitPersistentSchemaBackupScriptTests.cs adds offline static gates and bash -n validation on Linux.

There is NO persistent migration/role write, Deribit client use, broker order mutation, account binding or real financial-data insertion in this PR.

Expected owner-local marker, NOT YET OBSERVED:

TRADEOPS_PG17_SCHEMA_BACKUP_RESTORE_PASS source=tradeops_evidence major=17 schema_tables=5 owner_tables=5 independent_restore=PASS backup_checksum=PASS broker_rows=0 source_writes=0 deribit_calls=0

## Disaster recovery caveat and next workstream

This SHA-256-verified dump is NOT ENCRYPTED and still resides on the SAME WSL virtual disk as the persistent database. It does not qualify independent off-host recovery. Before the first real Deribit financial evidence import, establish encrypted independent storage/retention, separate decryption-key custody and test restoring the encrypted independent artifact. A checksum alone is not encryption.

These scripts deliberately refuse after broker records appear, so populated ledgers require a new backup and retention process with independent restore validation. Never automatically reuse this empty-only verifier in production.

After actual owner backup PASS, prepare a separately gated least-privilege writer role. Do not enable live Deribit, create account bindings, or import records without separate owner authorization. Never change legacy Orders/Fills, synthetic journals or Worker/API DI registration.

New-SHA independent .NET Gate A: NOT PROVEN. Keep PR Draft/unmerged.
