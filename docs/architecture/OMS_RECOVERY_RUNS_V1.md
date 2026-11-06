# Durable recovery runs v1

B4 implementation qualified offline, 2026-11-06. The original diagnostic entry records blocked reconciliation.
The explicit `RecoverAndPublishAsync` entry can now publish a qualified bounded baseline
and persist READY; this is not permission to send or proof of live trading readiness. It is not
registered in the existing read-only host, and no persistent mainnet database has
been migrated or granted additional privileges by this change.

## Qualified publication and runtime eligibility

`deribit-quiescent-recent-v1` requires an actual current economic-account subscription
ACK (global orders/trades/changes/portfolio), a quiet owned replay window, matching
full-account inventories, native clock, all required currency histories and validated
order/fill/position/balance models. The consumed window, including overlap and I/O,
must remain within a conservative 25-minute recent-order retention bound. Longer
outages remain blocked. Historical unfilled-order coverage is not fabricated.

Direct native receipts bind local commands; label/quantity similarity is insufficient.
An unresolved send without a direct receipt still blocks. Reconciled started/ambiguous
commands, attempts, outbox, evidence document, immutable recovered state, scoped head
and lifecycle event commit in one PostgreSQL transaction. Failure rolls them back.

Scoped committed evidence versions cover commands/attempts/outbox, captured facts,
incidents, receipts and operator decisions. Publication locks command rows before the
version row, then compares the version observed by the single MVCC ledger reader.
This prevents publishing an assessment superseded by committed account evidence;
it is not physical-send fencing. Future evidence mutations invalidate runtime eligibility.

`OmsRuntimeRecoveryGate` requires both the current live capture revision and an exact
database publication/run/generation/version binding. Capture ingress invalidates the
revision before commit; cash/position changes invalidate it even without an order fill.
Consumption is currently limited to five seconds after assessment. Saved JSON cannot
restore the opaque live proof. `OmsRecoveryRunHead.CanIncreaseRisk` remains false;
B5 risk admission and single physical sender ownership are separate required work.

The document persists the exact coherence problems as well as underlying evidence.
New recovery tables/contracts are first released as schema v1 with this B4 change;
none have been activated in the retained mainnet journal. Existing command v1,
private-event v2 and native-receipt v1 encodings are unchanged.

Scoped operator review is qualified with an actual restricted database role. Its only allowed adoption
concerns the exact observed external native order and open-order fingerprint. It
creates no local command or direct receipt and cannot clear hard recovery problems.
Actual database-role permissions authenticate the actor; decisions are immutable,
versioned and withdrawable. An acceptance expires after ten minutes or stops applying
when native terms differ. Permission provisioning is owner-only, never automatic.

`DeribitRecoveryRunEngine` binds the account-wide command/attempt/outbox/private
snapshot, scoped account observations, order queries and active capture replay to
one account, epoch, outage interval and run. The replay window opens **before**
the combined journal read, remains open during venue/account I/O, and is drained
and revalidated by the existing gate. There is one journal snapshot, not two
independently read private/command views. A bad journal blocks venue reads.

`IOmsRecoveryRunStore` is an Application port. Its PostgreSQL implementation adds:

| Table | Purpose |
|---|---|
| `oms_recovery_runs_v1` | Immutable start request, epoch, scoped generation, schema and digest |
| `oms_recovery_heads_v1` | Current account run and fail-closed state |
| `oms_recovery_events_v1` | Immutable lifecycle/alert reasons |
| `oms_deribit_recovery_results_v1` | Immutable validated assessment, original journal evidence and scoped account observations |
| `oms_evidence_versions_v1` | Scoped committed evidence revision; not a physical sender lease |
| `oms_recovered_account_states_v1` | Immutable qualified baseline, native checkpoint, state digest and evidence version |
| `oms_recovery_external_reviews_v1` | Immutable scoped operator accept/withdraw decisions, exact evidence digest and actual actor role |
| `oms_recovery_review_permissions_v1` | Owner-provisioned scoped database-role review permissions |

The result also archives the actual bounded query/addressed responses used by the
gate and a reproducible candidate order/fill model. Native fills deduplicate by
exchange identity; collisions have no arbitrary winning leg. Missing current
reports leave order status unknown. Only validated account observations enter
the candidate positions/balance. Historical and replay coverage gaps stay explicit;
the candidate remains unpublished and is not a restart checkpoint.

The diagnostic path is `Recovering -> Reconciling -> Blocked`; qualified publication
can finish at `Ready`. Failures, cancellation or explicit
connection/gap/account invalidation record `Disconnected`. These states deny
risk-increasing orders. There is no public "set READY" function. The runtime must
complete a fresh qualified recovery after restart; reading an old result never
restores freshness or trading permission.

New starts allocate a monotonic account-scoped generation. Late completion or stop
from a superseded run cannot alter the current head. Beginning the same run with
identical input is idempotent and cannot reset state; the same ID with different
input is a collision. Result, blocked head and lifecycle event commit atomically.
Re-delivery of the same result after a lost commit acknowledgement is idempotent;
a conflicting result preserves the original. Only qualified publication can resolve
started/ambiguous commands from their existing direct native receipts; the old request,
attempt count, canonical encodings and native receipts remain unchanged.

The additive migration is `20261105210000_AddOmsRecoveryRunsV1`. History/result
UPDATE, DELETE and TRUNCATE are denied by append-only triggers; Down refuses a
nonempty run history. Evidence stays private in the scoped database. Events record
fixed reason codes, without exception strings, credentials or HTTP payloads.

The new document has schema 1 and its own canonical digest. Identity value objects
have explicit scalar codecs. Decimal values use invariant text inside this new
document to preserve the original scale of embedded v1 commands and v2 private
facts across PostgreSQL jsonb normalization. Existing canonical encodings and
hashes are unchanged. Reads verify schema, capacity, digest and run/scope binding.

The implementation passed offline B4 acceptance. Qualification is offline,
using actual production readers, scripted native transport, restricted database
roles and real PostgreSQL transactions. A blocked live legacy account is not a
live READY result; unresolved old ACKs remain blocked rather than becoming receipts.

`OmsRetainedRecoveryArchiveV1` retains the original query facts of the latest
qualified checkpoint, including its prior retained facts. The versioned ledger
reader validates the immutable source document/digest and reads this checkpoint
inside the same RR transaction as inbox/commands/receipts/version. It does not
invent accepted inbox observations or deserialize runtime authority. Model/universe
construction deduplicates the retained archive with subsequent WebSocket facts;
advancing the query window never silently discards recovered fills. A bad digest,
foreign scope, native identity collision or capacity violation blocks recovery.
Clock-controlled actual-PostgreSQL regression cases cover two later recoveries
after the native order lookup retention, with and without a late WebSocket duplicate.

## Runtime composition

`Worker:Mode=DeribitAccountRecovery` explicitly selects a separate composition.
Both execution switches must be explicitly false. It registers no legacy workers
or order-entry clients. `TradeOpsCaptureDb` and `TradeOpsRecoveryDb` must use distinct
database identities on the same database. Startup checks existing grants; it never
migrates, grants permissions, substitutes an administrator or starts a sender.

The capture role retains the B3 capture-only privileges. The recovery role reads
the bounded ledger, inserts recovery metadata/state and may update only existing
commands/attempts/outbox for proven receipt reconciliation. It cannot insert a new
command, update evidence counters, modify operator permissions, write legacy
trading tables, delete history or perform DDL. The evidence arbitration function
locks the scoped counter without granting direct counter UPDATE.
Both helper functions revoke PUBLIC EXECUTE. Runtime/operator roles need explicit
function grants as well as their narrowly scoped table/column permissions. Derived
counter rollback is refused once recovery history exists; lower journal/private/
receipt migrations continue protecting their own original evidence.

`DeribitRecovery:InitialFromUtc` is a required trusted initial outage anchor, never
silently replaced with "now". Once a baseline exists, reconnect/restart/manual refresh
uses its persisted native checkpoint, even if later runs failed. Publication rejects
a shortened interval. Failed runs retain the last archive, without runtime authority.

The worker builds metadata from actual public catalogs before global capture and
refreshes it before recovery when four minutes old. A product absent from the active
capture decoder stops capture; it is never ignored. Supported normalization is
inverse USD-native futures and base-native options, with explicit native position
units; unsupported/expired metadata, linear position units, capacity limits and long
history windows produce blockers. Market-derived equity/base-size drift alone is
not treated as a cash/position mutation.

Each capture epoch triggers one recovery. An explicit `RequestRecovery()` coalesces
refresh requests; an unchanged blocked result is not automatically retried in a loop.
`ReadCurrentAsync()` returns a model only while the actual runtime gate is valid.
Shutdown/transport loss clears process authority and records a durable disconnect;
failure to confirm that record stops the host with a sanitized failure. No private
payloads or credentials enter host logs.

Currency-group collection is provided through `IDeribitCurrencyRecoveryQueries`
and the connector, using the native [currency order history](https://docs.deribit.com/api-reference/trading/private-get_order_history_by_currency)
and [currency trades](https://docs.deribit.com/api-reference/trading/private-get_user_trades_by_currency)
methods. This enumerates symbols beyond one retained order. Trade pagination uses
the opaque `start_id`, not a cross-instrument `trade_seq` or timestamp increment;
duplicate boundary records deduplicate, a stuck cursor blocks the batch. Unknown
instrument metadata refuses the complete result. Explicit future/option or `any`
queries cover one native currency; they do not prove coverage of the whole account. Historical-unfilled and
venue-coherence gaps remain explicit. No new live reads are automatically started.

`IDeribitAccountInventoryReader` discovers the current account scope without a
manifest filter, using [all open orders](https://docs.deribit.com/api-reference/trading/private-get_open_orders),
[positions with currency any](https://docs.deribit.com/api-reference/account-management/private-get_positions)
and [all currency summaries](https://docs.deribit.com/api-reference/account-management/private-get_account_summaries).
Native identity brackets the reads. Unknown products/trigger orders stay visible;
position references have no invented quantity units. Errors discard partial values.

`DeribitRecoveryUniverseBuilder` unions current instrument references with every
saved command and retained private fact. Zero current balances do not remove a
currency's outage history requirement. Native public metadata resolves terms;
unknown names and unsupported currencies remain explicit issues. Optional
`DeribitRecoveryUniverseSources` runs inventory and all required currency queries
inside the same actual replay window, then refreshes account observations and
checks inventory freshness after drain. Its inputs and issues persist in the same
run result. This records broader evidence; it still grants no account-wide or
coherence shortcut and is not enabled in the existing host by default.
