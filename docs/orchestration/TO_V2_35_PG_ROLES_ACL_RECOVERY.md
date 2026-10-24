# TO-V2-35 — full PostgreSQL 17 role/ownership/ACL recovery

## Starting point: owner-confirmed PR #36

Permanent PostgreSQL 17 `tradeops_evidence` contains exactly five
`broker_evidence` tables owned by the dedicated `tradeops_evidence_owner`
NOLOGIN role. Owner ran NOLOGIN writer installation at exact commit
`83af15406d07ee5931eb7ba206717b02142654da` and returned:

```text
TRADEOPS_PG17_WRITER_INSTALL_PASS database=tradeops_evidence major=17 role=tradeops_evidence_writer login=DISABLED binding_select=YES position_insert=YES update_delete=DENIED execution_insert=DENIED role_count=1 broker_rows=0 deribit_calls=0
```

Owner supplied evidence was recorded in Draft PR #36. All predecessor
passing SHAs are frozen, Draft and unmerged.

## Important recovery defect in earlier empty-schema backups

Previous archive scripts executed `pg_dump --no-owner --no-acl`.
Those are useful for testing five-table DDL and empty-data restores, but
**do not preserve actual table ownership or PostgreSQL GRANTs**. The
persistent cluster roles themselves are also global objects outside an
ordinary database `pg_dump`. Consequently those earlier dumps were
insufficient for full owner/ACL reconstruction.

## New artifacts, safe owner-local rehearsal only

- `deploy/deribit-evidence/restore-known-evidence-roles.pg17.sql`:
  deterministic password-free definitions for the **two** known NOLOGIN
  roles and approved writer session defaults. Refuses a non-empty or
  non-PostgreSQL-17 recovery target. It never creates a LOGIN role,
  and has no broker credential, database password or account data.
- `deploy/deribit-evidence/backup-restore-full-acl.pg17.sh`:
  read-only preflight on the exact labeled persistent PostgreSQL 17
  container and exact volume; requires five owner-owned tables, both
  NOLOGIN roles, grants/denials and **all evidence tables empty**.
  Produces a custom-format `pg_dump` with ownership and ACLs retained.
  Bundles the archive, known-role bootstrap SQL and SHA-256 manifest in
  a private 0700 WSL directory, with file mode 0600.
- It creates a new disposable PostgreSQL 17 instance with
  `--network none`, no ports, no existing volumes, tmpfs-only PGDATA,
  and nonce-checked cleanup. Runs known-role bootstrap into the fresh
  default `postgres` database; `pg_restore --create --exit-on-error`
  then recreates `tradeops_evidence` including its database privileges
  and per-table owners/GRANTs.
- Independently audits actual restored schema ownership, five table
  owners, role attributes, zero evidence rows and permitted/denied
  binding/position/execution privileges, and re-audits untouched source.
  Saves bundle only on verified PASS.
- `scripts/deribit/Verify-FullEvidencePg17RoleAclRecovery.ps1`
  fetches EXACT owner-selected Git HEAD, extracts two pinned source
  blobs, checks WSL path translation and Bash syntax before invocation.
- Offline unit tests verify no ACL stripping and prohibit operations
  on source Docker state.

The official PostgreSQL 17 pg_restore documentation specifies that
`--create` also restores database-level privileges, unless
`--no-acl` was specified. This procedure explicitly avoids that flag:
https://www.postgresql.org/docs/17/app-pgrestore.html

**Expected only — NOT YET QUALIFIED by owner execution:**

```text
TRADEOPS_PG17_ROLE_ACL_RECOVERY_PASS source=tradeops_evidence major=17 tables=5 owner_role=NOLOGIN writer_role=NOLOGIN grants=RESTORED denies=ENFORCED evidence_rows=0 independent_restore=PASS source_writes=0 deribit_calls=0
```

The expected bundle path is returned privately from the owner's local WSL2
system. No SQL containing real broker data is printed or committed.

## Limits and required next workstream

This bundle is currently **unencrypted** and is STILL stored in the same
WSL virtual disk as its source database. It is not a disaster-recovery
artifact until securely exported to independent storage using portable
authenticated encryption and separately verified after copy.
A further Draft workstream must implement a portable encrypted
off-WSL/off-host copy with restore/decryption validation before any
actual Deribit record import. Do not send the user secrets, SQL dump,
private archive, or password in chat.

The role bootstrap is deliberately **specific to the empty, current
known role/GRANT configuration** and will fail closed if the source
gains rows or different permissions. Do not reuse it automatically once
real data, role passwords or additional roles exist. Populate-aware,
versioned regular backups with retention and off-host recovery remain
separate essential work.

There is no migration, no role change, no account binding and no Deribit
API call in this workstream. Never touch other PostgreSQL containers,
TradeOps Orders/Fills, API/Worker wiring or execute a real trade.

New-SHA independent .NET Gate A: NOT YET VERIFIED.
Owner backup + isolated ACL restore Gate B: NOT YET RUN.
Keep PR Draft/unmerged until evidence is available.
