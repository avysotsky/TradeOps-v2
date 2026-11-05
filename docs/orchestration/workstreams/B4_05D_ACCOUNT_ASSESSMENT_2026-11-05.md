# B4-05d — account port integrated with Application assessment

2026-11-05. Implementation `c13f02a2760ca1474b5f9bad7599471d06c6e375`.
Scoped account observations now participate in the recovery assessment lifecycle,
with validation at final use. Qualification is **offline/DB-only**, not a new live
account snapshot, coherent recovery, durable projection or READY.

## Delivered

- Explicit `AssessAccountAsync` / `AssessAccountWithReplayAsync` compose the actual
  `IDeribitAccountSnapshotReader` port with the existing gate. No new constructor
  registration, connector/Infrastructure dependency, automatic host startup or
  execution switch is introduced. Existing order-only entries keep their behavior.
- Trusted scope/epoch/currency/kind, expected metadata/native units and bounded
  manifest are validated and copied before I/O or replay replacement. Invalid
  retained snapshots/selection evidence skip the account read.
- In active mode the account read occurs once inside the owned window, after order
  reads and before seal. The existing begin/drain/final-cut-validation/finally path
  is shared; exception/cancellation/supersession cleanup remains scoped to its window.
- Port result collections are copied on return. Scope/epoch/manifest/units, local
  invocation bounds, UTC balance/positions windows, signs and age are checked after
  replay drain, at assessment construction. Stale/cached/partial/foreign/malformed
  data give typed blockers, zero accepted rows and no balance-observed indication.
  Raw private amounts and position lists are not returned or persisted.
- Valid scoped observations omit only their availability blocker. All original
  order/history/ledger/replay blockers remain; AccountSnapshotCoherenceUnproven and
  AccountWideCoverageUnproven are always added. Stale active epoch/session/stopped
  coordinator also discards account acceptance. Independent valid account RPCs can
  remain observed when local replay fails from lost ACK; that still blocks recovery.
- AccountWideCoverage, VenueCoherent, AccountReady and DurableProjectionPublished
  remain false. Diagnostics are observations at that instant, not reusable leases
  or approved public payloads. Existing diagnostic enum values are preserved;
  durable canonical encoding/DB schemas are unchanged.
  [Contract](../../architecture/DERIBIT_RECOVERY_ACCOUNT_ASSESSMENT.md).

## Verification

Windows Release build: exit 0, zero errors, 15 existing warnings, prescribed SDK
and serial settings. Actual `b4-account-assessment` TRX: **445 total/executed/passed**,
zero failed/skipped; runner exit 0, environment/cleanup PASS. This includes the prior
394 cases and 51 new cases: 49 Application validation/lifecycle cases and two real
PostgreSQL integrations. The separate unchanged PowerShell diagnostics suite was
not rerun or counted as a new qualification in this increment.

New cases exercise fresh future/option observations, signed/native units, negative
balance, empty scoped arrays without invented zeros, invalid scope/epoch/metadata/
capacity, missing/duplicate/unmapped rows, malformed windows, cached/future results,
post-drain staleness, mutable caller/port collections, skipped reads for invalid
retained evidence, active epoch loss and exception/cancellation preserving a newer
window. Successful local replay/account availability cannot remove unrelated gates.

Both actual PG cases use the production retained reader and production account
connector (scripted HTTP). A real capture commit overlaps the account positions RPC
after the retained snapshot and before seal. One case validates the committed local
prefix; the other loses ACK after that same real commit, blocks replay and verifies
capture is attempted once. The durable fact remains readable in both cases. Valid
account observations remain counts only; AccountReady/publication stay false.

Evidence: `.tradeops/postgres-reuse/20261105T144034-b4-account-assessment-32756/`.
**323 normalized source hashes** matched. Test DLL SHA256:
`79DE87279DD3B6C15EF5E0CD784C425AAF8C90AD50115DB43DC2F2D06027D833`.
Guard before/after PASS; peer container lifecycle snapshots unchanged (empty diff);
owned clone/role/session cleanup PASS. The reusable test server/template is retained.
Owner launchSettings SHA256 remains
`CBDDB50FE5C8379A82B3DD8B482696625C7F2F5DFB0058D2FD1D423653398183`.

Venue results were scripted/loopback. No vault was decrypted, real venue session
connected, mainnet store opened or trade performed. The previous B4-05c scoped live
PASS remains historical evidence; this increment does not refresh it or reuse its
anonymous report as recovery facts. No durable account projection was published.

## Next increment

B4-05e: explicit bounded read-only host binding/qualification of `AssessAccountAsync`
with the existing saved native pin/key, public ETH/future metadata and the retained
SELECT-only journal reader. Build the actual trusted expected manifest before I/O;
emit only approved anonymous counts/typed blockers. The old qualification hosts
still use their previous entries and do not automatically call these new methods.
Do not repeat the old placement/cancel harness or unavailable terminal-order lookup.

Active replay requires a real server-owned capture session, not caller flags. Venue
snapshot/history coherence, complete relevant execution ledger, account-wide coverage,
durable projection/recovery gates and B5 physical fencing remain separate gaps.
B4/READY and #73 stay open. No new trading operation is authorized by this increment.
