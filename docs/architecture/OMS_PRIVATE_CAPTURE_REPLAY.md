# B4-04b — local capture receipt cut and bounded replay

B4-04c now provides the [explicit gate lifecycle integration](DERIBIT_ACTIVE_REPLAY_GATE.md).
The original base assessment still lacks active replay; the new entry can remove
only the local-buffer blocker. Venue coherence and account READY remain unqualified.

This adds an opt-in receipt/commit barrier to `OmsPrivateCaptureCoordinator` and
the existing Deribit private capture queue. It qualifies a **local** cut, not an
exchange-consistent REST snapshot. The B4-04a gate still reports CoherentCutUnavailable
and BufferedReplayUnavailable until its orchestration is explicitly integrated with
this active capture owner. No READY, projection, sender or durable schema changes occur.

## Ownership and ingress

Construct the coordinator with explicit `OmsPrivateReplayLimits` to enable tracking;
existing callers default to disabled and retain the legacy capture path. A replay
window requires the active session, current nonempty epoch, current runner generation
and healthy subscribed Recovering/AwaitingReconciliation status.

The opt-in Deribit adapter registers each parsed envelope **before** placing it in
the bounded capture queue. Registration validates scope/epoch/source and issues a
coordinator-owned receipt. Registration uses a short ingress lock, never waits for a
database commit, and therefore does not put a DB wait on transport heartbeat handling.
Receipts have no public constructor, are bound to the issuing coordinator/session/
epoch and can be attempted only once. They cannot be transferred to another owner.

Scope/epoch/status transitions are atomic with ingress registration. The existing
commit semaphore still serializes capture and account/epoch transitions; it is held
through the actual capture port return. Receiver-side receipt ordinals and committed
ordinals are process-local counters. They are not venue revisions, PostgreSQL commit
sequences or restart/checkpoint positions.

## Begin, seal, drain and read

`BeginReplayAsync` starts after the contiguous committed receipt prefix. Already
received but queued/uncommitted envelopes are therefore included in the overlap.
Only one window is retained; starting another window supersedes the previous token.

`SealReplayAsync` freezes the **received** high-watermark under the local barriers.
It waits for any current capture call to return, but does not mistake an empty
in-flight slot for a drained ingress queue. A cut can still contain queued receipts.
`ReadReplayAsync` returns PendingCommits and **zero entries** until the complete
prefix through that received watermark has committed. Later receipts can continue
to be captured durably but never enter the sealed cut.

Each returned entry preserves its receipt ordinal, immutable envelope and capture
Inserted/Duplicate disposition. Duplicate observations are retained as committed
returns; execution deduplication remains the reducer's responsibility. Canonical
observation digests detect conflicting reuse of a capture ID. An untracked direct
capture on an enabled coordinator invalidates replay while retaining durable capture.

Tokens must be revalidated through `ReadReplayAsync` immediately before later use.
A previously copied batch describes the binding at the time of that read, not a
perpetual lease. This is not physical sender fencing or a durable projection commit.

## Bounds and sticky failures

Limits are validated at construction: 1–10,000 retained observation returns,
1 KiB–16 MiB retained canonical event+observation bytes, 1–10,000 pending receipts,
and a monotonic maximum window lifetime (default two minutes, maximum ten).
The default row/byte/pending bounds are 1,024 / 2 MiB / 4,096. Row bounds also bound
the metadata/digest collections; byte bounds describe serialized canonical payloads,
not a promise about total CLR heap allocation. There are no background timers/tasks.

Overflow clears retained replay payloads and tracking maps; subsequent queued facts
can still reach the existing durable capture port. Gap/stop/collision/invalid input,
untracked capture, out-of-order commit, unknown capture result, failed/uncertain commit
and expiry invalidate the window. Sticky epoch faults cannot be cleared by simply
setting a healthy status; a new epoch is required. New epochs, account switches,
runner replacement, coordinator disposal and superseded windows revoke old cuts.
Invalid/stale/pending reads never return partial facts as a usable batch.

If capture committed and then its acknowledgement was lost, the receipt remains
failed/attempted and replay is unusable. The actual durable row remains available
through the scoped reader. This implementation does not resend that receipt or
invent a successful commit acknowledgement.

## Qualification and remaining coherence

Tests cover queue-before-begin overlap, pending commits, registration during slow
commit, sealed-prefix exclusion of later receipts, bounds, sticky faults, bindings,
runner/epoch replacement, monotonic expiry, supersession and foreign/reused tickets.
Actual PostgreSQL cases verify an independently readable committed prefix with
duplicate observations, a lost acknowledgement after a real commit, and the opt-in
adapter queue/parser/receipt path into PostgreSQL. Venue traffic is scripted; no
new mainnet event, private session, trade or persistent-store operation is performed.

`CanReplay=true` proves only a complete bounded **local receipt/commit prefix** in
that active binding. `VenueCoherent=false` and `AccountReady=false` are explicit.
The stream may lack venue-level sequence/coverage guarantees, and historical/REST
indexing and snapshot boundaries remain separate requirements. Never replace those
requirements with caller flags or extend the query cutoff to receive time.

Next: integrate the active window/cut lifecycle into evidence-gate orchestration,
including cut revalidation and bounded drain handling. Venue snapshot/history coverage,
complete execution ledger, positions/balances, durable publication and B5 sender
fencing remain unqualified. This local prefix is not a restart-safe checkpoint.
