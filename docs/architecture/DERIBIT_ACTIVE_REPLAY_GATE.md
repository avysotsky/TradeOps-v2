# B4-04c — active replay lifecycle in the evidence gate

`DeribitRecoveryEvidenceGate.AssessWithReplayAsync` is an explicit Application entry
for a server that already owns an active opt-in capture coordinator/session. The
existing assessment-only entry and qualification hosts are unchanged; no active
capture is started automatically. There is no new connector/persistence dependency,
durable schema, sender, publication or account READY transition.

## Lifecycle and authority

The method validates the complete trusted gate request and drain limits before
changing any replay window. Scope must match the supplied session; BeginReplay also
checks the real active epoch, subscribed health and runner generation. It accepts no
caller-supplied "complete" flags or copied replay batches.

The owned window begins **before** retained snapshot/query/known-order assessment.
Captures overlapping any of those reads are included in the local receipt prefix.
After the existing assessment returns, the method seals the actual window and reads
its coordinator-owned cut. Pending commits are drained within an explicit deadline.
The deadline covers waiting to acquire the seal/read commit barrier as well as the
pending-prefix poll loop; it does not replace the caller/port deadlines for snapshot
and venue I/O. Defaults are five seconds and ten milliseconds; wait is bounded to
1 ms–30 s, polling to 1–250 ms and no longer than the wait budget.

A successful local prefix must bind to the exact scope/epoch/window/cut, with a
contiguous receipt range and validated WebSocket envelopes and capture dispositions.
Trusted currency/native units are checked for the selected instrument. Other
manifest instruments remain distinct; total and selected-instrument counts are
reported separately and do not create account-wide coverage. The opaque cut is
read again after inspection for final active-owner/epoch/gap validation.

Only a validated local prefix removes **BufferedReplayUnavailable** from the base
assessment. Snapshot/query/known-read errors and historical-unfilled, order-history,
coherent-cut and execution-ledger blockers remain intact. Buffered observations are
not applied to the reducer, returned as private payloads or published durably by this
slice. The result is a diagnostic snapshot of the validation, not a reusable lease.

## Failure, cancellation and cleanup

Timeout returns BufferedReplayDrainTimedOut plus a typed DrainTimeout problem;
invalid/stale/gapped/expired/superseded replay returns BufferedReplayInvalid. Neither
clears BufferedReplayUnavailable, and invalid/partial results report zero accepted
buffered observations. Caller cancellation propagates instead of becoming a normal
successful assessment; exceptions from the base ports also propagate.

Every path after BeginReplay uses `finally` to abandon its own window. The new
coordinator abandonment operation uses only the short ingress lock: it neither waits
for/cancels an independent durable capture nor discards its stream-owned receipt
ledger. Capture can complete after an assessment times out. Cleanup never removes a
newer window that superseded this one. Abandoned tokens cannot later be replayed.

Private replay payloads, native order IDs and the opaque cut token are not returned.
The existing scope binding in Evidence remains private and must be redacted from
public logs. AccountReady and
DurableProjectionPublished remain false. Final revalidation is not future process
fencing: later durable publication still needs its own active-binding transaction
and physical send authority is a separate B5 concern.

## Qualification and next work

Adversarial tests cover begin-before-snapshot/query, pending drain completion,
timeout during pending and in-flight commit, binding/epoch/runner/gap changes,
supersession without cross-cleanup, cancellation/exception cleanup, invalid requests,
bad snapshots, other instruments, invalid selected units, overflow and expiry.
Owned-clone PG cases combine the actual retained reader, active coordinator and
committed capture port with the gate. Lost ACK after real commit leaves the durable
row intact, rejects local replay and never resends capture.

This is offline/DB-only qualification with scripted venue results. It is not a new
live capture, coherent exchange snapshot or trading-readiness proof. Existing live
qualification paths still use the base assessment because they do not own an active
replay-enabled stream. No vault, real account session or persistent mainnet store is
used for this increment.

Next collect and normalize scoped read-only positions/balances with explicit units,
coverage and freshness, while separately qualifying venue snapshot/history coherence
and complete relevant execution ledgers. Local drain completion does not establish
those proofs or justify moving the query cutoff to receive time. Durable projection/
recovery gates and physical sender fencing remain subsequent work.
