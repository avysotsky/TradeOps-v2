# Deribit order recovery projection — B4-01

`DeribitOrderRecoveryProjector` is a deterministic application-layer reducer over
the existing private facts v2 and observation v1 contracts. It rebuilds a bounded
native order ledger from prior committed data plus one closed recovery batch.
It performs no I/O or dispatch, writes no legacy Orders/Fills and never grants
account READY or send permission. It is deliberately Deribit-specific.

## Inputs and comparison

- Trusted full account scope, current subscription epoch, required outage start
  including safety overlap, and a closed cutoff. Foreign scopes, stale current
  epochs and facts/receipts beyond the cutoff block consistency. Prior epochs are
  allowed only in the persisted set. Current receipts before the outage start are
  rejected; old venue timestamps in a newly received history response are valid.
- Persisted facts include the full retained execution ledger for every selected
  order, not just trades since disconnect. The reducer cannot reconstruct missing
  historical executions from a cumulative order quantity.
- Recovery coverage explicitly describes the interval and completion of open
  orders, order history, trade history and buffered replay. Missing pages,
  incomplete replay or gaps block consistency. These are caller-supplied evidence
  assertions; implementing/qualifying the query collector remains a separate step.
- Maximum 10,000 facts per input set. Exceeding capacity fails explicitly and
  requires bounded partitioning by the caller; overflow never silently drops data.

Each order is partitioned by currency and native order ID inside the full scope.
Executions use the existing currency + trade ID + own order leg canonical identity.
Identical persisted/WS/history receipts count once; changed quantity, price, fee or
other immutable facts under the same execution identity block the result. No
conflicting execution is arbitrarily selected. Native amount units remain intact.

Order reports sort by venue time, with deterministic canonical tie ordering.
Different reports at one venue timestamp are ambiguous, cumulative regression is
blocked, and a terminal order cannot reopen. Changed terms/size require explicit
revision recovery rather than an inferred replace. Terminal cancelled orders can
retain partial fills. Filled/rejected quantities and the latest cumulative total
must agree with the distinct execution ledger. Unknown cumulative values remain
unknown; arithmetic overflow produces a blocking issue and null total.

Absence from open orders does not imply cancellation. Every previously known order
needs current explicit order evidence; otherwise its projected state is null.
Exchange-only orders remain visible and require review. An execution with no order
report produces a missing-evidence issue, without an invented total/state.

## Consumer boundary and remaining work

`IsConsistent` means only that this bounded order-ledger comparison has no issues.
Projections in an inconsistent result are observations for diagnosis, not trusted
trading state. All blocking issues must remain visible to the caller. Retained
integrity incidents and quarantined alternative facts must not be discarded by a
future reader merely because the primary inbox contains one accepted execution.

This iteration does not implement the production reader/checkpoint adapter,
paginated recovery queries, coherent positions/balances, local intent/label linking,
durable issue/projection publication, recovery orchestration or physical sender
fencing. Hosts and execution flags are unchanged. A production reader must validate
full scope, supported schemas, hashes, dispositions/incidents and transaction
boundaries before supplying these inputs. Test-only SQL decoding is not registered
in a host.

The `b4-order` acceptance suite combines the 144 B3 cases, 28 journal cases,
28 private capture cases, 42 pure reducer cases and two PostgreSQL replay cases:
244 total. It uses scripted venue messages and isolated test databases. No live
credential or additional exchange order is used. PostgreSQL cases reopen physical
connections, reconstruct committed observations, preserve quarantined alternatives
and verify no legacy Orders/Fills writes.
