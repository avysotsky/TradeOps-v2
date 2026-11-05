# B4-04b — local receipt cut and bounded committed capture replay

2026-11-05. Implementation `e304917`.
The local capture receipt/commit cut is qualified offline/PG. Venue-consistent
REST/history coherence, gate integration, durable publication and READY remain open.

## Delivered

- Opt-in replay limits on the existing capture coordinator; existing callers remain
  disabled by default. No connector persistence dependency or durable schema change.
- Adapter registers validated owner/session/epoch receipts before queue admission.
  Ingress registration does not wait for a DB commit, preserving heartbeat progress.
  Runner lease and generation are published atomically; empty epochs are refused.
- Begin includes already queued/uncommitted overlap. Seal fixes the received prefix;
  reads return no entries until every receipt through that boundary has committed.
  Later receipts still capture durably but are excluded from the sealed cut.
- Opaque process-local window/cut/receipt tokens; stale scope/epoch/runner, foreign
  owners, reuse, supersession and stop cannot produce usable replay. Sticky gaps,
  uncertain commits, collisions, overflow, invalid/untracked capture, reordering and
  expiry invalidate replay. Bounds cover rows, canonical bytes, pending receipts
  and monotonic duration; no background timers or global settings are introduced.
- Returned replay preserves canonical envelopes and committed Inserted/Duplicate
  dispositions. It is not a PostgreSQL cursor, venue sequence or execution permission.
  [Contract](../../architecture/OMS_PRIVATE_CAPTURE_REPLAY.md).

## Verification

Windows Release build: exit 0, zero errors, 15 existing warnings, prescribed SDK and
bounded build flags. Final `b4-replay` actual TRX: **275 total/executed/passed**, zero
failed/skipped; runner exit 0, environment/cleanup PASS. The suite combines the prior
209 recovery/gate cases, 41 additional existing stream cases (11 parser cases overlap
the 52-case stream suite), and 25 new cases: 22 replay unit cases and three actual PG
cases. Final review added explicit nonempty-epoch refusal and atomic runner identity;
the earlier 274-case pass is superseded by this final run.

New cases exercise queued-before-begin overlap, pending commit blocking, nonblocking
registration during slow commit, post-cut exclusion, three overflow budgets, sticky
gaps, epoch/account/stop/runner replacement, out-of-order returns, collision/unknown
capture results, monotonic expiry, supersession, untracked capture, foreign/reused
tokens and invalid source binding. Existing stream/socket/coordinator cases verify
the disabled default path and loopback transport behavior remain intact.

Actual PostgreSQL proofs, confined to owned disposable clones:

- A sealed received prefix remains pending while its second queued receipt is not
  committed. Once committed, replay has Inserted/Duplicate entries matching the
  independent production reader: one accepted fact, two capture observations.
- A wrapper loses ACK **after real commit**. Replay is unusable with no entries;
  the production reader still sees the one committed fact/observation. Exactly one
  capture attempt occurred; no resend is performed.
- The opt-in scripted WS adapter/parser/queue registers receipts and commits through
  the real PG capture port. A valid one-entry local prefix is independently readable.

Evidence: `.tradeops/postgres-reuse/20261105T111522-b4-replay-11069/`.
**308 normalized source hashes** matched. Final test DLL SHA256:
`904C67AFA7A968910B711F7930607FA9B366744F09F9EC846536463EB0F6F1C2`.
Resource Guard before/after PASS; peer container lifecycle snapshots unchanged;
owned test clone/role/session cleanup PASS and test server retained.
Owner launchSettings SHA256 unchanged:
`CBDDB50FE5C8379A82B3DD8B482696625C7F2F5DFB0058D2FD1D423653398183`.

No vault was unlocked, real venue session connected, mainnet store opened or trade
performed for this iteration. Scripted/loopback venue traffic and actual clone commits
must not be presented as newly qualified live events, live recovery or READY.

## Meaning and next increment

CanReplay=true proves only a complete bounded local receipt/commit prefix in the
binding checked at read time. VenueCoherent=false and AccountReady=false are explicit;
tokens require revalidation before use. Capture cancellation/epoch checks are not
physical sender fencing. The evidence gate is deliberately not wired to remove its
BufferedReplayUnavailable/CoherentCutUnavailable blockers in this slice.

B4-04c: integrate the active window/cut lifecycle with evidence-gate orchestration,
with bounded drain handling and final scope/epoch/token revalidation. Keep local
prefix completeness separate from qualified venue snapshot/history coherence and
do not synthesize complete flags or extend query cutoff to receive time. Complete
execution ledgers, positions/balances, durable projection/gates and B5 fencing remain;
issue #73 stays open. No new trade is needed or authorized.
