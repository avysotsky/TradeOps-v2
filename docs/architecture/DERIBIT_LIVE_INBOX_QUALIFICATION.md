# Isolated live WS → inbox qualification

Explicit extension of the [private-session probe](DERIBIT_PRIVATE_SESSION_PROBE.md).
The default probe remains DB-free. `-WithIsolatedInbox` selects a separate acceptance
executable; no API/Worker defaults, production binding or trading switch is enabled.

```powershell
./scripts/deribit/Deribit-PrivateSessionReadOnlyLocal.ps1 -ExpectedHead <reviewed-40-character-SHA> -ObserveSeconds 45 -WithIsolatedInbox
```

## Source, credentials and target

The Windows launcher builds the exact clean HEAD before unlocking its existing
CurrentUser-DPAPI credential/profile. It forwards bounded JSON through redirected
stdin to the reviewed WSL runner and short-lived .NET child. Real API keys never
become command-line arguments, environment exports or files. Source SHA is checked
again afterward and compared with the WSL capture SHA.

There is no input parameter for arbitrary DB, schema, SQL, host or exchange URI.
The runner reuses only the marked `tradeops-tests-v1` PostgreSQL17 server, with exact
image/config/ownership validation, internal network, no published port, 384 MiB,
0.5 CPU, 128 PIDs and 256 MiB tmpfs data. Persistent databases are not used. Before/
after Resource Guard and peer-container snapshots are required. A server failing
ownership validation is not queried during cleanup.

The acceptance project links the existing guarded fixture source under a separate
compile-time namespace and embeds the same schema resources. Its fingerprint must
match the regular fixture. One marked empty clone reuses the migrated template and
holds the same server-wide acceptance lease. There is no alternative weaker cleanup
or migration path outside this owned test server.

## Least privilege and lifecycle

Before any venue socket, the tool creates a marked generated application role and
acquires the existing scoped owner gate. Grants are limited to capture SELECT/INSERT,
UPDATE required for row locks, incident SELECT/INSERT and sequence USAGE. The role
is not owner/superuser/DB or role creator/replication/RLS-bypass. The existing gate
also refuses legacy/execution write privileges.

Only the setup session and generated role receive terse/no-statement/no-error-parameter
logging defaults to prevent private failing-row details entering DB logs. These
[logging controls](https://www.postgresql.org/docs/17/runtime-config-logging.html)
and [role settings](https://www.postgresql.org/docs/17/sql-alterrole.html) do not change
global server configuration or durability.

The diagnostic scope identifies the authenticated entity from the protected ID/type
observation. A subaccount marker does not manufacture a parent account. In this
variant the scope may be stored **only in the disposable clone**; it is not promoted
to production hierarchy. Independent ownership/hierarchy qualification remains false.
No sender or legacy Orders/Fills processor is constructed.

Validated notifications use the real coordinator → scoped owner → PostgreSQL store.
After successful socket/writer shutdown, inbox/observation/incident counts are checked
against actual capture returns and normalized events; legacy tables must remain empty.
Unresolved tails, ambiguity, collisions or transport failures cannot qualify capture.

The owned lease/source is closed, generated-role ownership verified, grants/role removed,
and only the idle marked clone dropped. No FORCE, global pool reset or server restart.
The runner independently checks leftover clones/roles/sessions and emits its redacted
result only after cleanup, guard and peer-snapshot checks.

## Result boundaries

| Status | Meaning |
|---|---|
| `LIVE_INBOX_CAPTURE_PASS` | Healthy bounded session plus real event commit/dedup in this isolated store |
| `LIVE_INBOX_CONNECTED_NO_EVENTS` | Restricted DB path and healthy session connected; zero events, no live commit proof |
| `LIVE_INBOX_FAILED` | Guard, transport, consistency or cleanup failed; unknown results are not invented as zero |

Neither success establishes account-wide coverage, complete recovery, production
hierarchy or trading readiness. Metadata is restricted to qualified inverse BTC
future/options groups. No order is created to generate events. Epochs remain RECOVERING.
`MutatingCalls` counts exchange mutations; owned test provisioning and DB row counts
are reported separately.

Offline `b3-inbox` runs 117 cases: previous 108, five fixture/store checks including
main/subaccount real commit/dedup and quiet/wrong-ID outcomes, plus four fixture cases.
All exchange traffic there is synthetic; the separate live mode reports actual counts.
See [test runner](../testing/OMS_POSTGRES_TESTS.md).
