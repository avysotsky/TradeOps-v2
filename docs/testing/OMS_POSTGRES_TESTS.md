# Isolated reusable OMS PostgreSQL 17 acceptance

Owner decision, 2026-11-05: during B4 development, run only affected behavior.
The complete acceptance is reserved for completion of the whole B4 stage. The older
suite commands below describe historical scopes, not a sequence to rerun after edits.

```powershell
./scripts/testing/Run-OmsPostgresTests.ps1 -Suite targeted `
    -Filter 'FullyQualifiedName~DeribitRecoveryRun|FullyQualifiedName~DeribitRecoveryLedgerAssessmentTests' `
    -ExpectedTests 38
```

`targeted` is the default and requires an explicit filter and expected count. It
keeps the same owned disposable databases, resource guards, exact TRX count checks
and cleanup checks as acceptance. A filter is passed as an argument, never shell code.

Windows PowerShell, from the repository:

```powershell
./scripts/testing/Run-OmsPostgresTests.ps1 -Suite b2
./scripts/testing/Run-OmsPostgresTests.ps1 -Suite b3
./scripts/testing/Run-OmsPostgresTests.ps1 -Suite b3-stream
./scripts/testing/Run-OmsPostgresTests.ps1 -Suite b3-host
./scripts/testing/Run-OmsPostgresTests.ps1 -Suite b3-session
./scripts/testing/Run-OmsPostgresTests.ps1 -Suite b3-inbox
./scripts/testing/Run-OmsPostgresTests.ps1 -Suite b3-testnet-order
./scripts/testing/Run-OmsPostgresTests.ps1 -Suite b3-mainnet-eth
./scripts/testing/Run-OmsPostgresTests.ps1 -Suite b4-order
./scripts/testing/Run-OmsPostgresTests.ps1 -Suite b4-order-reducer
./scripts/testing/Run-OmsPostgresTests.ps1 -Suite b4-reader
./scripts/testing/Run-OmsPostgresTests.ps1 -Suite b4-binding
./scripts/testing/Run-OmsPostgresTests.ps1 -Suite b4-collector
./scripts/testing/Run-OmsPostgresTests.ps1 -Suite b4-known-orders
./scripts/testing/Run-OmsPostgresTests.ps1 -Suite b4-gate
./scripts/testing/Run-OmsPostgresTests.ps1 -Suite b4-replay
./scripts/testing/Run-OmsPostgresTests.ps1 -Suite b4-replay-gate
./scripts/testing/Run-OmsPostgresTests.ps1 -Suite b4-account
./scripts/testing/Run-OmsPostgresTests.ps1 -Suite b4-account-host
./scripts/testing/Run-OmsPostgresTests.ps1 -Suite b4-account-manifest
./scripts/testing/Run-OmsPostgresTests.ps1 -Suite b4-account-assessment
./scripts/testing/Run-OmsPostgresTests.ps1 -Suite b4-unified
./scripts/testing/Run-OmsPostgresTests.ps1 -Suite b4-terminal
./scripts/testing/Run-OmsPostgresTests.ps1 -Suite b4-ledger
./scripts/testing/Run-OmsPostgresTests.ps1 -Suite b4-receipts
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
`b3-testnet-order` adds 17 cases to the 117 inbox/fixture cases, 134 total.
These use scripted exchange replies and real PostgreSQL for journal/capture,
including one placement, lost place/cancel ACK, unresolved cancellation, an
unexpected fill and refusal to repeat an attempt.
HTTP requests assert POST and short REST-session scope, rejecting WS connection
tokens or another session. The separately approved
[one-order live testnet scenario](../architecture/DERIBIT_TESTNET_ORDER_QUALIFICATION.md)
does not run in this suite.
`b3-mainnet-eth` adds 10 mainnet-policy cases to the 134 cases above, 144 total.
Its exchange responses remain scripted; its injected PG databases are disposable
test clones and never claim persistent retention. The separate
[mainnet ETH qualification](../architecture/DERIBIT_MAINNET_ETH_QUALIFICATION.md)
uses an independently marked persistent store and is not launched by this suite.
`b4-order` runs 247 cases: those 144, 28 journal cases, 28 private capture cases,
45 pure order-recovery cases and two committed PostgreSQL observation replay cases.
It checks deduplication, missing evidence, collisions, stale epochs, account scope,
partial-cancel/late-fill races and incomplete coverage. It performs no live exchange
requests and never changes the retained mainnet qualification store. The
[order projection contract](../architecture/DERIBIT_ORDER_RECOVERY_PROJECTION.md)
describes the boundary and remaining account recovery work.
`b4-order-reducer` selects the 47 recovery cases alone for changes confined to
the reducer/result contract; it uses the same marked test server and cleanup guards.
`b4-reader` selects 87 cases: those 47 plus 15 composition and 25 actual PostgreSQL
reader cases, including corruption injection, scoped incidents and a blocked
in-flight capture. RLS policies prove the SELECT-only reader actually uses a
read-only repeatable-read transaction. All injection is confined to disposable
clones; no live DB or exchange is used. See the
[reader contract](../architecture/OMS_PRIVATE_RECOVERY_READER.md).
`b4-binding` selects the 87 reader/recovery cases plus 12 retained-store binding
cases, 99 total. Provisioning and privilege injection use disposable clones only;
this suite never opens the persistent store, decrypts a vault or requests the venue.
The separate [retained-store qualification](../architecture/DERIBIT_RETAINED_RECOVERY_READER.md)
uses the existing marked persistent database without migrations or trading.

`b4-collector` selects the 99 reader/binding cases, 11 existing shared private parser
cases and 27 collector cases, 137 total. Collector HTTP is scripted: fixed read-only
origins/methods/grants, account checks before/after, recent/historical subsets,
offset and trade-sequence pagination (including equal timestamps), dedup/collision,
explicit historical-unfilled coverage gap, bounds, protocol/transport/status errors,
caller cancellation and incomplete-batch clearing. Only the existing PG cases open
owned test clones. Vaults, live venues and the persistent store are not used by the suite.
See the [query contract](../architecture/DERIBIT_RECOVERY_QUERIES.md) for the separate
live enumeration and its coverage limitations.

`b4-known-orders` selects those 137 cases plus 36 retained-reference selection and
addressed-read cases, 173 total. Scripted venue tests cover found/missing/unsupported,
identity/term/time/quantity checks, partial-set clearing and failures/cancellation.
It never decrypts a vault or requests a live account. The separate
[known-order qualification](../architecture/DERIBIT_KNOWN_ORDER_READS.md) uses the
existing SELECT-only persistent reader and one addressed request per retained seed;
a completed negative lookup is not recovered state or READY.

`b4-gate` adds 36 application orchestration/gate cases to those 173, 209 total.
These adversarial port fixtures exercise stale/foreign snapshot/query/known bindings,
source/window/provenance/terms corruption, contradictory reports, partial/missing
evidence, retention-aware planning, old open orders, external references, quiet scopes
and cancellation. They assert that found/exhausted data does not imply coherent
recovery, account READY or durable publication. Existing PG tests still use owned
clones only. The [separate live assessment](../architecture/DERIBIT_RECOVERY_EVIDENCE_GATE.md)
records typed BLOCKED reasons through the existing SELECT-only persistent reader.

`b4-replay` selects 275 cases: the 209 gate/recovery cases, 41 additional stream
cases (11 parser cases overlap the 52 stream cases), and 25 new replay cases.
The 22 pure replay cases exercise receipt/commit prefix semantics, pending queue,
bounds, stale binding/runner/epoch and fail-closed lifecycle. Three actual PG cases
verify durable duplicate observations, lost ACK after real commit without resend,
and the opt-in scripted adapter queue into the capture port. All venue traffic is
scripted/loopback and PG resources are owned disposable clones. This qualifies a
[local replay cut](../architecture/OMS_PRIVATE_CAPTURE_REPLAY.md), not venue coherence,
live events, production binding, durable checkpoints or READY.

`b4-replay-gate` selects 294 cases: the prior 275 plus 17 active lifecycle/drain
cases and two PG integrations. They cover owned begin/seal/drain/final validation,
binding loss, bounded timeout even during an independent commit, exception/cancellation
cleanup, supersession, metadata separation and preservation of unrelated blockers.
Actual PG tests combine the production retained reader/capture with overlapping
buffered events and lost ACK after commit without resend. All venue results remain
scripted/loopback; only owned disposable clones are used. This qualifies the
[active local replay gate](../architecture/DERIBIT_ACTIVE_REPLAY_GATE.md), not venue
coherence, live events, durable publication or READY.

`b4-account` selects 336 cases: the prior 294 plus 42 scripted account-snapshot
cases. They check native position units/signs, negative cash balance, currency/product
scope, empty scoped arrays without synthesized zeros, consumption-time freshness,
backward/slow observations, changed native identity, unmapped/duplicate rows, malformed
fields, bounds, read-only grants and cancellation. No vault or real venue is used;
the existing PG cases still run only in owned disposable clones. This qualifies the
[scoped observation reader](../architecture/DERIBIT_SCOPED_ACCOUNT_SNAPSHOT.md), not
live positions, account-wide coverage, coherent recovery, publication or READY.

`b4-account-host` selects 364 cases: those 336 plus 28 qualification-host cases.
They check metadata-before-auth, native subaccount binding, output redaction,
incomplete/empty results, read-only request allowlists, invalid inputs, cancellation
and freshness revalidation at diagnostics construction. All venue results are
scripted. The Windows wrapper also runs `Test-AccountSnapshotDiagnostics.ps1`:
32 cases execute only the real output-validator function, without the vault/process
entry, covering approved pass/incomplete/refused reports and malicious or malformed
payload rejection. Neither suite is a live account qualification.

`b4-account-manifest` selects 394 cases: those 364 plus 30 explicit ETH/future
manifest cases. They cover dated future observations, pre-auth public metadata
validation, bounds/age/environment/native units, inactive/expired/unmapped refusal,
mode/purpose isolation, native identity and private output suppression. The 32
PowerShell validator cases include v2 schema/policy/count binding and legacy single
mode compatibility. Venue responses remain scripted; only owned disposable PG
clones are used. Live account qualification requires a separate explicit invocation.

`b4-account-assessment` selects 445 cases: the prior 394 plus 49 Application
account assessment cases and two actual PG integrations. They check scope/epoch/
manifest/unit/window/age validation at use, empty scoped results, malformed/partial
ports, collections frozen across awaits, pre-I/O refusal, replay overlap, stale-after-
drain behavior, epoch changes and exception/cancellation/supersession cleanup. The PG
cases use the production account connector with scripted HTTP and a real retained
reader/capture commit during account RPCs, including lost ACK without resend. All
account/coherence/ledger/READY/publication gates remain fail-closed. This qualifies
the [Application composition](../architecture/DERIBIT_RECOVERY_ACCOUNT_ASSESSMENT.md)
offline/DB-only, not a new live snapshot or successful recovery.

`b4-unified` selects 471 cases: the prior 445 plus 24 host/input/metadata/source/
redaction cases and two real PG host integrations. The production order, known-order
and account connectors use scripted HTTP; the PG cases read an actual retained
terminal order and an actual integrity incident. Counts remain unchanged, expired
terminal orders are not looked up again and incident evidence suppresses private
reads. The Windows wrapper additionally runs `Test-AccountRecoveryDiagnostics.ps1`:
30 real-validator cases, with no vault/venue/process entry. This is offline/DB-only
verification of the [unified host](../architecture/DERIBIT_UNIFIED_READONLY_ASSESSMENT.md);
a real read-only qualification is a separate explicit invocation.

`b4-terminal` selects 521 cases: the prior 471 (updated to the explicit retained
terminal policy) plus 50 rule/gate/PG cases. They cover zero/partial/full native
quantities, missing/extra/late fills, dedup across snapshots/queries, immutable
execution collisions, wrong binding/snapshot/source, report conflicts, fee omissions,
overflow, multiple own order legs and preservation of every global recovery gate.
Three actual PG cases derive proof from the production reader for zero fill, complete
fills and missing fills without journal changes or another expired order lookup.
The Windows diagnostics-validator suite has 30 cases, including v2 schema and
validated-terminal count/blocker consistency. This qualifies the
[retained evidence policy](../architecture/DERIBIT_RETAINED_TERMINAL_EVIDENCE.md),
not a full execution ledger, current venue state or READY.

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
