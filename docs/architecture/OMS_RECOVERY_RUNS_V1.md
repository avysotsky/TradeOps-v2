# Durable recovery runs v1

Current B4 work, 2026-11-06. This path records an actual failed/blocked reconciliation;
it does not yet publish a recovered working account or authorize READY. It is not
registered in the existing read-only host, and no persistent mainnet database has
been migrated or granted additional privileges by this change.

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

The result also archives the actual bounded query/addressed responses used by the
gate and a reproducible candidate order/fill model. Native fills deduplicate by
exchange identity; collisions have no arbitrary winning leg. Missing current
reports leave order status unknown. Only validated account observations enter
the candidate positions/balance. Historical and replay coverage gaps stay explicit;
the candidate remains unpublished and is not a restart checkpoint.

State is `Recovering -> Reconciling -> Blocked`; failures, cancellation or explicit
connection/gap/account invalidation record `Disconnected`. These states deny
risk-increasing orders. There is no public "set READY" function. The runtime must
complete a fresh qualified recovery after restart; reading an old result never
restores freshness or trading permission.

New starts allocate a monotonic account-scoped generation. Late completion or stop
from a superseded run cannot alter the current head. Beginning the same run with
identical input is idempotent and cannot reset state; the same ID with different
input is a collision. Result, blocked head and lifecycle event commit atomically.
Re-delivery of the same result after a lost commit acknowledgement is idempotent;
a conflicting result preserves the original. Existing dispatch phases and venue
receipts are never repaired or rewritten by the recovery store.

The additive migration is `20261105210000_AddOmsRecoveryRunsV1`. History/result
UPDATE, DELETE and TRUNCATE are denied by append-only triggers; Down refuses a
nonempty run history. Evidence stays private in the scoped database. Events record
fixed reason codes, without exception strings, credentials or HTTP payloads.

The new document has schema 1 and its own canonical digest. Identity value objects
have explicit scalar codecs. Decimal values use invariant text inside this new
document to preserve the original scale of embedded v1 commands and v2 private
facts across PostgreSQL jsonb normalization. Existing canonical encodings and
hashes are unchanged. Reads verify schema, capacity, digest and run/scope binding.

Remaining B4 work: complete supported recovery universe/history policy and actual
venue coherence proof, derive and atomically publish the working order/fill/
position model, wire its runtime readiness gate, and qualify the complete failure
lifecycle. Durable recording of a blocker does not resolve that blocker.

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
