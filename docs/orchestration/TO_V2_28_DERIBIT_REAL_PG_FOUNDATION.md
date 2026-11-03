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


## Owner's WSL2 inventory and dedicated PostgreSQL 17 proposal (2026-10-23)

Owner-supplied read-only Docker listing:

- `jobradar-pr-autotest-postgres` — `postgres:17-bookworm`, up 3 days; belongs to JobRadar tests.
- `external-agent-postgres` — `postgres:17-bookworm`, healthy, up 3 days; belongs to external-agent.
- No running or stopped container named for TradeOps; `docker volume ls` shows no named TradeOps evidence volume. Anonymous volumes exist and ownership is not yet proven. **Never infer that either existing PG17 container or volume is free for TradeOps.**
- The 2026-10-23 owner-provided listing is an observation only. No Docker inspect, persistent DB query, or backup verification has been performed by the assistant.

The safe next target is a **new independent** `postgres:17-bookworm` service in
`deploy/deribit-evidence/docker-compose.pg17.yml`, isolated from the repository-root
legacy `docker-compose.yml` and from existing JobRadar and external-agent resources.

The proposal has these explicit identities:

| Item | Proposed value |
| --- | --- |
| Compose project | `tradeops-deribit-evidence` |
| Container | `tradeops-deribit-evidence-pg17` |
| PostgreSQL major | 17 (actual server must be checked after startup) |
| Database | `tradeops_evidence` |
| Data volume | `tradeops-deribit-evidence-pg17-data` |
| Host publish | `127.0.0.1:54329 -> 5432` (WSL2/Windows loopback) |
| Credential bootstrap secret | WSL-only absolute path supplied via `TRADEOPS_EVIDENCE_PG_BOOTSTRAP_PASSWORD_FILE` |
| Position-import DB role | `tradeops_evidence_writer`, non-superuser, insert-only for evidence snapshots/positions |
| Migration/bootstrap | Separate owner-controlled review; NO startup SQL |

The Compose file does NOT mount or reuse JobRadar / external-agent volumes, does NOT
set a default password, does NOT embed Deribit credentials, and does NOT invoke a
migration. The bootstrap PostgreSQL administrator is distinct from the eventual
import role. The role proposal in
`deploy/deribit-evidence/TO_V2_28_PROPOSED_WRITER_ROLE.sql` refuses to operate
outside the exact dedicated DB or PostgreSQL 17 and refuses to overwrite an
existing writer. It must be executed only after the isolated schema is explicitly
approved, migrated and verified; password setup is an interactive private step.

### Safe read-only operator preflight

These may be run from Windows PowerShell before approving any deployment:

```powershell
wsl.exe -- docker ps -a --format "table {{.Names}}\t{{.Image}}\t{{.Status}}\t{{.Ports}}"
wsl.exe -- docker volume ls
wsl.exe -- docker inspect --format '{{.Name}} {{range .Mounts}}{{.Type}}:{{.Name}}->{{.Destination}} {{end}}' jobradar-pr-autotest-postgres external-agent-postgres
```

This reveals mount names, **not environment variables, passwords, SQL rows,
trade IDs, vault files or API credentials**. Docker inspection of configuration
`Config.Env` is forbidden for shared containers because it can expose secrets.

For the proposed deployment, do NOT run `docker compose up` until explicitly
approving the new independent PostgreSQL instance and local secret path.
After approval, a private password file is created outside Git with owner-only
permissions, the Compose config is validated, then `up -d` starts the
independent service. Schema migration and real account import still need
**distinct explicit approvals**.

**Prohibited commands for this workstream:** `docker volume prune`,
`docker system prune --volumes`, `docker compose down -v`,
`docker volume rm` and any migration against JobRadar/external-agent.
Keep a retention-controlled off-volume backup and perform a verified test restore
before importing real Deribit snapshots. Volume persistence alone is not backup.

**Acceptance labels:** PR code/build Gate A NOT PROVEN; disposable PG17 Gate B
NOT PROVEN; owner target/backup/migration Gate C NOT APPROVED; actual Deribit
positions persisted NOT PROVEN; executions persisted NOT IMPLEMENTED.


## Owner approval and one-shot empty PG17 deployment handoff

The owner explicitly approved **creation of the dedicated empty PostgreSQL 17
container and persistent volume only**. This approval does NOT cover executing
`TO_V2_28_BROKER_EVIDENCE_SCHEMA_PROPOSED.sql`, roles SQL, account bindings,
real broker API access, execution importer, live API/Worker integration, or
modification/deletion of any previously existing Docker resource.

Two exact-SHA scripts were added to this Draft PR:
- `scripts/deribit/Deploy-EmptyEvidencePg17.ps1`: PowerShell 5.1 owner-local
  entry point; fetches branch and refuses HEAD != owner-supplied exact SHA,
  then stages only the pinned Compose and Bash files in a disposable temp
  directory, syntax-checks WSL Bash and executes it.
- `deploy/deribit-evidence/deploy-empty-pg17.sh`: WSL preflight and
  one-time creation; rejects collisions with existing container, volume,
  Compose network and listening WSL port; generates a 32-byte CSPRNG
  bootstrap password in WSL under
  `~/.local/share/tradeops/deribit-evidence-pg17/` (private, 0600),
  validates pinned Compose, starts **only** the isolated PostgreSQL service,
  and confirms container healthy, PostgreSQL major 17, new named volume
  mounted and absence of `broker_evidence` schema.

The operator invokes the exact-SHA PowerShell source from the immutable Git
object after checking fetched branch HEAD, without changing the repo checkout
or Windows machine-wide execution policy. A real deployment is accepted ONLY
when the owner reports
`TRADEOPS_PG17_EMPTY_DEPLOY_PASS ...`. Branch or script creation is **not**
evidence that the container was started.

On failure: stop and inspect non-secret error codes. Do not automatically
retry a partially created container/volume; never `down -v` or prune.
Make sure the independent container is correctly initialized before
provisioning a separate non-superuser writer or installing schema.
