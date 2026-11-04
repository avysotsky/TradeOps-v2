# Isolated reusable OMS PostgreSQL 17 acceptance

Windows PowerShell, from the repository:

```powershell
./scripts/testing/Run-OmsPostgresTests.ps1 -Suite b2
./scripts/testing/Run-OmsPostgresTests.ps1 -Suite b3
./scripts/testing/Run-OmsPostgresTests.ps1 -Suite b3-stream
./scripts/testing/Run-OmsPostgresTests.ps1 -Suite b3-host
./scripts/testing/Run-OmsPostgresTests.ps1 -Suite b3-session
./scripts/testing/Run-OmsPostgresTests.ps1 -Suite b3-inbox
./scripts/testing/Run-OmsPostgresTests.ps1 -Suite fixture
```

The wrapper builds with the existing Windows .NET SDK, bounded MSBuild settings and
no restore, then executes the portable assembly in Ubuntu, outside `global.json`.
It does not install SDKs or use GitHub Actions. Build dependencies must already be
restored. A failed build prevents test execution. For an already built assembly:

```powershell
wsl.exe -d Ubuntu -u root -- bash /mnt/d/Projects/TradeOps-v2/scripts/testing/oms-postgres.sh b2
```

`b2` checks the actual TRX for all 28 B2 cases (23 PostgreSQL + 5 pure contracts),
with no failed/skipped cases. `b3` checks 28 scoped capture cases (11 PostgreSQL +
17 contracts), including ordinary application-role, upgrade/Down, atomic commit
and concurrent/idempotent observation tests. `b3-stream` checks 52 targeted lifecycle,
parser, coordinator, real loopback transport, PG pipeline and existing Deribit cases.
`b3-host` checks those 52 plus 26 startup/binding/composition cases and five real PG
host/ownership cases, 83 total. It uses scripted venue sockets. Its connection-loss
case terminates only the owned backend of a generated application role inside its
generated test clone; no container/server restart or persistent DB is used.
`b3-session` checks those 83 plus nine protocol-only probe cases and 16 granted-scope
policy cases, 108 total. All venue traffic in this suite remains scripted/loopback;
it never unlocks a vault or performs live requests. The actual bounded Windows live
probe has [separate evidence and limits](../architecture/DERIBIT_PRIVATE_SESSION_PROBE.md).
`b3-inbox` checks 117 cases: the 108 session cases, five linked-fixture/store cases
and four fixture cases. Its venue responses are synthetic; actual commit/dedup uses
restricted-role PostgreSQL. The optional [live inbox mode](../architecture/DERIBIT_LIVE_INBOX_QUALIFICATION.md)
is separate and never runs from these offline tests.
`fixture` checks four additional integration cases
covering committed cross-connection visibility, clone isolation/pool disposal,
fresh migration preparation, ownership refusal, crash remnants and stale templates.
Logs, TRX, counters, durations, container snapshots and guard results stay under
ignored `.tradeops/postgres-reuse/`. No credentials are printed or saved to evidence.

## Server boundary and lifecycle

Only `tradeops-tests-v1-pg17` on internal `tradeops-tests-v1-net` is used. Both have
the exact `tradeops.tests.owner=tradeops-tests-v1` label. The runner validates the
image, mounts, limits, network and configuration before starting an existing
container. It never removes/restarts an already running server. Existing containers
are compared before/after each run; Resource Guard must pass before and after.

Limits remain 0.5 CPU, 384 MiB RAM, 128 PIDs, 30 server connections, and one CPU for
the test process. PostgreSQL data is on a **256 MiB bounded tmpfs**, with no persistent
volume or published port. Only one completed schema template is retained. This cache
survives ordinary test invocations while the server runs. Explicitly stopping the
test container discards its tmpfs: starting it later reinitializes an empty marked
test cluster, and the next acceptance recreates the template. Durability settings
remain `fsync=on`, `synchronous_commit=on`, `full_page_writes=on`; this is still a
test-only in-memory filesystem, not production power-loss qualification.

The fixture independently requires PostgreSQL 17, the dedicated
`tradeops_test_admin` login, database `postgres`, cluster name `tradeops_tests_v1`,
the server ownership comment, expected encoding/locale and durability settings.
Missing configuration, an ordinary persistent server or `tradeops_dev` fails
**before CREATE/DROP**. Historical disposable B2 runners without these markers
are intentionally no longer accepted. No persistent infrastructure is repurposed.

## Template, fresh mode and connection ownership

Each case owns a unique `to_test_<32 hex>` database. Ordinary cases clone a frozen,
empty migrated `to_tpl_<24 hex>` database. Fingerprints use embedded migration
sources, model snapshot, DbContext/configurations, initialization SQL, EF/Npgsql
versions and an explicit fixture seed/configuration version. Line endings are
normalized. Documentation changes and arbitrary Git SHAs do not invalidate it.
When extensions, initdb configuration or fixture seeds change, update the explicit
version/configuration contract too. Template migration connections are unpooled
and disposed before `ALLOW_CONNECTIONS=false`; no sessions remain on the source.

Both existing migration Up/Down cases explicitly use `FreshMigrations`: create
from `template0` and apply the actual full EF chain. Upgrade, connection-loss,
restart and role scenarios must choose fresh preparation/connections where their
subject requires it. A clone must never replace testing an actual migration path.

Database ACLs are configured after cloning because PostgreSQL does not copy them.
PUBLIC has no database access. The restricted-role test explicitly grants CONNECT
and its existing limited table/sequence permissions, without superuser, CREATEDB,
CREATEROLE, replication or TRUNCATE privileges. Immutable triggers stay enabled.

Ordinary cases/roles own their own NpgsqlDataSource with a pool allowing 20 connections,
enough for real 12/16-way races. Query assertions use that same datasource. The four
crash/restart/lost-ACK cases explicitly disable pooling to retain fresh physical
connections; admin, migration and other explicit fresh connections remain unpooled.
Disposal closes only the owned pool before database removal, allowing up to three
seconds for its backends to disappear from PostgreSQL's activity view. A genuine
remaining session still causes refusal. There is no global `ClearAllPools`, outer
rollback transaction, or forced connection termination. Real commits and independent
connections remain part of the tests.

## Cleanup and refusal

A PostgreSQL session lease allows one fixture at a time across acceptance processes,
bounding disk use without serializing the independent connections inside each case.
A second runner fails `TEST_SERVER_BUSY` instead of disrupting the first.
Cleanup accepts only narrow generated names, the exact ownership comment and owner,
and **zero active connections**. DROP never uses FORCE. Marked idle crash remnants
and obsolete/incomplete owned templates are removed on the next run. Restricted
test roles also carry an ownership comment; DROP ROLE must succeed without touching
dependencies in other databases. Unmarked, foreign or still-connected resources
cause refusal and remain intact. Do not relax these checks to make a run pass.

Official references: [PostgreSQL templates](https://www.postgresql.org/docs/17/manage-ag-templatedbs.html),
[CREATE DATABASE](https://www.postgresql.org/docs/17/sql-createdatabase.html),
[Npgsql datasource/pooling](https://www.npgsql.org/doc/basic-usage.html).
