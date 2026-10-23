# TO-V2-28 — Real Deribit read-only position evidence / persistent PostgreSQL foundation

**Stacked base:** TO-V2-27 Draft PR #29, frozen HEAD `6022143aa00f20cac87b3a34d96954ac15c7f347`.
**Safety state:** Draft only. No production migration, live DB session, broker session, or Gate A/B acceptance performed by this PR creation.

## Audited facts (at the frozen base)

- `docker-compose.yml` currently defines PostgreSQL **16**, named volume `tradeops-postgres`, host port 54321, superuser `postgres` and an example password embedded in Compose. A named volume persists while retained, but this is not evidence of a deployed, backed-up, least-privilege PostgreSQL **17**. Do not treat this configuration as an approved production target.
- `TradeOpsDbContext` owns live `Orders`, `Fills` and EF migrations. `Fill.ExchangeFillId` is globally unique and `Fill.OrderId` requires an existing local Order; importing external history there would be incorrect. No legacy model or migration is modified.
- TO-V2-27's `DeribitSyntheticExecutionJournal` is locked to a disposable `tradeops_api_smoke_*` DB and must remain synthetic-only. Its Gate A was 992/992 PASS at the exact preceding SHA. Owner PostgreSQL Gate B was not run.
- Existing Deribit mainnet `DeribitLiveReadOnlyClient` is non-DI, not an `IExchangeClient`, and has only fixed GET reads. The owner previously observed two futures position entries, zero open orders and zero BTC executions in the trailing 7-day checked window; no actual execution import has been verified.

## Contents of this initial real-data slice

1. `scripts/deribit/sql/TO_V2_28_BROKER_EVIDENCE_SCHEMA_PROPOSED.sql` is an **unapplied** additive SQL proposal for a separate `broker_evidence` schema. It creates an owner-approved credential binding, immutable position snapshots and rows, an account-scoped execution evidence table, and independent per-mode trade query records. SQL execution is never automatic.
2. `DeribitLiveReadOnlyClient.ReadPositionEvidenceAsync` fetches one fixed authenticated `private/get_positions?currency=any` mainnet GET and returns typed signed native sizes and request/response timestamps. Existing count-only probe behavior is unchanged.
3. `DeribitRealReadOnlyEvidencePgStore` requires a pinned host, port, database and non-redirected session identity, and pre-existing exact owner-approved provider/mainnet/credential-fingerprint binding. It cannot create a schema or binding. One PostgreSQL transaction inserts one immutable snapshot plus its position rows. An existing snapshot ID, unknown binding or database error fails and rolls back rather than overwriting data.
4. `tools/TradeOps.DeribitRealEvidenceCapture` is a one-shot opt-in CLI; it has no API/Worker registration. It has separate broker-read and DB-write phases. It reads Deribit, then stores a position snapshot ONLY after explicit opt-in, exact DB target env, and a pre-provisioned account binding. Output is aggregate-only.
5. Offline guard and fake HTTP response tests cover scope validation, native signed units, account fingerprint, live legacy model isolation and the additional GET.

**No trade-history rows are imported in this slice.** The `execution_observations` schema is preparatory, not proof of historical completeness or implemented idempotent replay. A later change must expose complete typed bounded history pages, reject conflicting immutable execution replays, record `historical=true` and `false` independently, and ensure retry-safe coverage. Do not run any execution backfill until that code is reviewed.

## Account-identity limitation / fail-closed policy

The SHA-256 fingerprint of a locally held Deribit API **client ID** is **not** an exchange account ID and is not proof of ownership. An owner must explicitly attest that this particular dedicated **read-only** credential belongs to the intended Deribit mainnet account, and insert the binding using a privately executed provisioning process. If the key is rotated or subaccount scope changes, pause import and re-approve the binding; never map a random user-provided account string or symbol to an account. Do not publish the raw client ID, secret, fingerprint, binding UUID, balances, account or trade rows.

## Mandatory owner's target approval BEFORE any write or migration

Confirm privately, without credentials in chat or GitHub:

- actual server and major version (`SHOW server_version;`; aim PostgreSQL 17), exact host/port/database, persistent volume/storage, dedicated schema owner, and separate **non-superuser** evidence-writer login with least-privilege grants;
- backups (including real retention and successful tested restore), target DB identity, recovery path, existing schemas, concurrent workloads, TLS for remote hosts, and no reliance on disposable acceptance databases;
- complete SQL review and explicit permission to execute the **specific** additive schema migration against the **specific** persistent database; also separately approve account binding provisioning;
- independent WSL2 Gate A for this exact PR SHA: restore + Release build + all xUnit tests with 0 failed and 0 skipped, cleanup 6/6; disposable PostgreSQL schema/migration, transaction/rollback/concurrency Gate B; then owner-controlled persistent migration and capture Gate C.

**Rollback:** the initial schema is isolated. BEFORE migration, create and test a full backup. On a failed empty migration, the explicit SQL transaction rolls back. After successful real-data ingestion, DO NOT casually DROP the schema; stop the importer, retain original data and backup, use a reviewed data-export/restore or isolated schema switch. Any destructive cleanup requires separate approval.

## CLI contract (NOT ready to execute until owner Gate C)

All values remain on the owner's computer. Do not place them in PR comments, scripts, command history or logs.

```text
TRADEOPS_DERIBIT_REAL_PG_CAPTURE_CONFIRM=OWNER_APPROVED_DERIBIT_POSITION_EVIDENCE_PG_V1
DERIBIT_LIVE_READONLY_CLIENT_ID=<owner-local DPAPI decrypted value>
DERIBIT_LIVE_READONLY_CLIENT_SECRET=<owner-local DPAPI decrypted value>
TRADEOPS_DERIBIT_REAL_PG_CONNECTION=<private PostgreSQL connection string>
TRADEOPS_DERIBIT_REAL_PG_APPROVED_HOST=<approved host>
TRADEOPS_DERIBIT_REAL_PG_APPROVED_PORT=<approved integer>
TRADEOPS_DERIBIT_REAL_PG_APPROVED_DATABASE=<approved persistent DB>
TRADEOPS_DERIBIT_REAL_PG_APPROVED_USER=<approved non-superuser writer>
TRADEOPS_DERIBIT_REAL_PG_BINDING_ID=<owner-provisioned binding UUID>
```

These environment variables are consumed only by the explicit CLI; there is no automated import task, no migration-on-startup and no broker mutating route. The previous DPAPI vault file must not be copied to WSL or GitHub. An additional exact-SHA Windows PowerShell 5.1 wrapper is needed before safe owner-local testing; this PR deliberately does not replace approved TO-V2-26 helpers.

## Status and next qualification

- GitHub branch and Draft PR: created from frozen #29 only; no merge.
- New tests: **not yet independently executed**. Previous 992 passing tests are NOT evidence for this head.
- Persistent PostgreSQL major version, topology, permissions and backup: **NOT VERIFIED**.
- Real Deribit position persistence: **NOT RUN** until owner approval.
- Genuine Deribit executions persisted: **NOT IMPLEMENTED / NOT PROVEN**.
- Deribit broker account mutations: **NONE added**.
