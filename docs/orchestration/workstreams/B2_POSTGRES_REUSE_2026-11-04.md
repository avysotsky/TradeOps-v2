# B2 PostgreSQL test reuse — 2026-11-04

Baseline: `418916b9f93c141b7b5f7a7f4be53d32d4bd1544`.
Branch: `TradeOps/b2-postgres-template-reuse`.
Owner-approved prerequisite before [B3 #73](https://github.com/avysotsky/TradeOps-v2/issues/73).
This changes test preparation and local acceptance tooling only; no production
schema, trading runtime, UI, SDK or WSL configuration changes.

## Result

The final B2 suite passes **28/28**, including the same 23 PostgreSQL case
identities and 5 pure contracts as the earlier raw TRX. Sum of PostgreSQL case
durations decreases from **340.934 s to 155.900 s** (**2.19x**, **54.3%** less time).
The reported VSTest test duration decreases from about 5m46s to **2m39s**.
The final run includes one schema-template rebuild caused by the improved provider
fingerprint, both fresh migration cases and all four explicitly unpooled
crash/restart/lost-ACK cases. This is the final acceptance measurement.
The old slow baseline suite was not rerun.

Earlier development measurements were 132.918/133.485 s with all journal pools
enabled, then 153.315 s after preserving fresh recovery connections. The faster
intermediate figures are **not** the benchmark claimed for the final revision.
The original server container ID/start time/restart count remain unchanged across
all runs. The final template is subsequently reused by fixture acceptance without
rebuilding. Results are observations, not a guaranteed future speedup or a complete
breakdown of authentication, migrations, cloning and test operations.

Four new real PostgreSQL fixture acceptance cases also pass **4/4**, with no skips.
They verify isolation, independent connections and commit visibility, fresh migration
preparation, ownership refusal, active-session refusal, abandoned owned databases,
and bounded replacement of obsolete templates. They do not replace the B2 cases.

## Implementation and safety review

- A dedicated reusable PostgreSQL 17.11 container and internal network are marked
  with exact ownership labels. No published ports or permanent volumes. The runner
  checks image/configuration/mounts/resource limits before starting an existing
  stopped container. A running server is kept intact across ordinary invocations.
- The SQL fixture independently validates server ownership, dedicated admin identity,
  PG17, encoding/locale and durability settings before database creation/removal.
  Missing configuration or a normal persistent database fails closed.
- Schema fingerprint embeds actual migration files, model snapshot/configurations,
  DbContext, initialization SQL, provider versions and explicit fixture seed version.
  No Git SHA or documentation hash. One frozen, migrated empty template is retained;
  every ordinary case receives a separate database clone.
- Database-level ACLs are explicitly applied after cloning. PUBLIC has no database
  access; the generated restricted application login explicitly receives CONNECT
  and its existing limited OMS table/sequence grants. Immutable triggers and the
  TRUNCATE denial remain effective.
- Both existing migration Up/Down cases use fresh databases from `template0` and
  the actual migration chain. No outer rollback transaction replaces real commits.
- Ordinary database/role cases own their own datasource/pool, allowing 20 connections
  for the real 12/16-way races. Four crash/restart/lost-ACK cases explicitly disable
  pooling. Admin/migration and explicit fresh connections are also unpooled.
  Only the owned datasource is disposed; bounded waiting allows its server backends
  to finish closing before the same strict idle check. No global ClearAllPools or FORCE.
- A server fixture lease bounds simultaneously allocated test databases across
  processes. It does not serialize the independent connections inside a race case.
  Cleanup requires exact ownership, narrow generated names and no active sessions.
  Unmarked/foreign/active resources are refused, not reset. Restricted crash-role
  cleanup uses DROP ROLE without touching dependencies in other databases.

CPU/RAM limits match the old B2 runner: **0.5 CPU / 384 MiB**, one CPU for the test
process. Disk is capped by a **256 MiB tmpfs**; observed data usage after B2 is 71 MiB
and a post-run memory sample is about 92 MiB. These samples are not peak measurements.
The tmpfs cache lasts while the container runs; an explicit stop/start reinitializes
the disposable test cluster and rebuilds its template on demand. Durability settings
are not disabled, but this is not production power-loss qualification.

## Executed validation and evidence

- Windows SDK 10.0.401 build: **PASS**, 0 errors, 15 pre-existing analyzer warnings
  on recompilation; subsequent unchanged build is incremental with 0 warnings.
- Fixture integration acceptance: **PASS 4/4**, 0 failed/skipped.
- Original B2 contracts/integration: **PASS 28/28**, 0 failed/skipped.
- Missing PostgreSQL: **PASS for fail-closed behavior**; a real B2 test invocation
  without the connection variable produces the expected 1/1 failure with
  `POSTGRES_ACCEPTANCE_NOT_RUN`, verified in its separate TRX.
- Restricted-role journal/recovery/dedup/payload immutability/TRUNCATE: **PASS**.
- Real Up/Down migration cases and nonempty rollback refusal: **PASS**.
- Raw TRX case identity/count comparison with baseline: **PASS**.
- Resource Guard before/after and unchanged neighboring-container snapshots: **PASS**.
- Cleanup: no test clone, test role or test-database connection remains; one frozen
  current template is retained. Owner's launchSettings SHA-256 remains unchanged.
- Full unrelated backend suite, browser E2E, live exchange and SIGKILL harness:
  **NOT_RUN**. No execution/recovery readiness claim follows from this test change.

Local evidence under ignored `.tradeops/postgres-reuse/`:
Final B2: `20261104T110742-b2-25493/`; final fixture:
`20261104T111103-fixture-30801/`; aggregate `summary.json`. These include raw TRX,
summaries, guard output, database inventory and before/after Docker snapshots.
Initial development failures are retained too: one setup query used a removed
configuration lookup (fixed to the PG17 catalog); one fixture assertion required
an explicit `regclass::text` cast. Neither is counted as acceptance PASS.
A later concurrency run exposed the short server-side socket-close race;
bounded own-database waiting fixes it without terminating sessions. The Windows
wrapper's path conversion was also corrected to direct `wsl.exe --exec wslpath`.
The separate missing-PG refusal TRX is under `.tradeops/b3-01-inventory/`.

Usage and lifecycle details: [OMS_POSTGRES_TESTS.md](../../testing/OMS_POSTGRES_TESTS.md).
The next development stage remains B3 #73 read-only private-event capture.
