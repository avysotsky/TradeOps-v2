# B4-04c — active replay lifecycle in evidence-gate orchestration

2026-11-05. Implementation `35cc3f6`.
The application gate now owns begin/seal/drain/final-validation/cleanup for a supplied
active capture session. This qualifies local replay integration offline/PG, not venue
coherence, full account recovery, durable publication or READY.

## Delivered

- Explicit `AssessWithReplayAsync` entry validates trusted request/binding/options
  before creating a window, begins before snapshot/query/known reads, then seals the
  actual coordinator-owned cut. No caller flags or copied replay batches are trusted.
- Bounded drain covers pending-prefix polling and waiting for the seal/read commit
  barrier. Default five seconds, maximum thirty. Caller cancellation and underlying
  port exceptions propagate; no successful empty assessment is manufactured.
- Full local receipt-range and envelope binding checks, selected instrument/native
  unit validation and final opaque-cut re-read. Total vs selected instrument counts
  remain separate. Buffered observations are counted, not applied to the reducer.
- Only BufferedReplayUnavailable is removed after a valid local prefix. Invalid or
  timed-out replay retains it and adds an explicit invalid/drain-timeout blocker.
  Snapshot/history/coherence/ledger blockers, AccountReady=false and
  DurableProjectionPublished=false remain.
- Finally abandons only the assessment's own window without waiting for or cancelling
  independent durable capture. Newer superseding windows survive older cleanup.
- Existing assessment-only/qualification hosts remain unchanged; the new entry requires
  a server-owned active replay-enabled session. No automatic stream/venue startup,
  new database permissions, durable schema change or trading switches are introduced.
  [Contract](../../architecture/DERIBIT_ACTIVE_REPLAY_GATE.md).

## Verification

Windows Release build: exit 0, zero errors, 15 existing warnings, prescribed SDK and
bounded build flags. Actual `b4-replay-gate` TRX: **294 total/executed/passed**, zero
failed/skipped; runner exit 0, environment/cleanup PASS. It includes the prior 275
cases and 19 new cases (17 orchestration cases, two actual PostgreSQL integrations).

New adversarial cases cover window-before-snapshot/query, successful pending drain,
timeouts during pending and in-flight commit, epoch/scope/runner/gap loss, supersession
without cross-cleanup, query/cancellation/exception cleanup, invalid requests/options,
bad snapshots, other manifest instruments, wrong selected metadata, overflow and expiry.
Positive local replay cannot remove unrelated recovery blockers or grant READY.

Actual PG cases, inside owned disposable clones:

- Real retained reader sees the snapshot before a query-overlapping real capture
  commit. The gate validates one buffered observation; an independent later read
  sees one accepted fact/observation. The local buffer blocker is removed while
  CoherentCutUnavailable and account/durable-publication prohibitions remain.
- The capture port commits and its wrapper loses ACK. The durable fact remains
  readable, local replay is rejected, buffer-unavailable/invalid blockers remain,
  and capture is attempted exactly once without resend.

Evidence: `.tradeops/postgres-reuse/20261105T114443-b4-replay-gate-6618/`.
**311 normalized source hashes** matched. Test DLL SHA256:
`76B4D9B6B12962B893E38F51FAA2A7C80C2DF4E67BA5A9B908B1969BFC20BE63`.
Resource Guard before/after PASS, peer container lifecycle snapshots unchanged,
owned test clone/role/session cleanup PASS; the test server is retained.
Owner launchSettings SHA256 remains:
`CBDDB50FE5C8379A82B3DD8B482696625C7F2F5DFB0058D2FD1D423653398183`.

Venue results are scripted/loopback; no vault was decrypted, real venue session
connected, mainnet store opened or trade performed. No new live-event/coherent-recovery
claim follows from these actual clone commits. No durable projection was published.

## Next increment

Collect/normalize scoped read-only positions and balances with trusted native units,
currency coverage and explicit freshness/incompleteness. Continue to qualify venue
snapshot/history coherence and complete relevant execution ledgers separately; never
replace them with local drain completion, caller flags or receive-time cutoff shifts.
Durable projection/recovery gates and B5 physical fencing remain; issue #73 stays open.
No new trading operation is needed or authorized by this code increment.
