# B4-03b — bounded read-only Deribit recovery queries

`IDeribitRecoveryQueries` is an application port. Its implementation lives in
`TradeOps.Connectors.Deribit`, depending only on Application/Domain. It never opens
PostgreSQL, captures durable facts, registers a sender or advances account readiness.
This is an instrument-scoped collection primitive, not an account-wide recovery gate.

## Scope and transport

Each query carries the full trusted account scope, an epoch, an explicitly manifested
instrument, UTC outage interval and positive overlap. The existing trusted capture
plan pins native account ID/type and instrument units/currency. The connector validates
the binding and verifies the native account through `get_account_summary` before and
after collection. API-key names and opaque digests never establish native identity.

Authentication explicitly requests a unique short REST session, `expires:300`,
`trade:read account:read` and denied wallet/block categories. Unexpected or wider
grants are refused. The transport allows only auth, account summary, open orders,
order history and user trades by instrument, on the fixed mainnet/testnet origin.
Credentials stay in POST bodies; private requests carry a session bearer token.
There are no retries or order/cancel/position methods. Callers must provide an
HttpClient with redirects, cookies, default credentials and retries disabled.
The separate qualification executable constructs this client explicitly.

## Bounds, sources and pagination

The outage interval is at most seven days with 1 ms–1 hour overlap. Lower bounds
round outward to venue milliseconds. Each invocation has a 60-second deadline;
requests are sequential with at least 150 ms spacing. The query caps page size
(1–1000), pages per source (1–20) and distinct normalized facts (1–10,000).
Replies are capped at 512 KiB each and 8 MiB in aggregate, fetched incrementally.
Limits, malformed replies, transport/rate-limit errors, stalled pagination, conflicts
or changed account bindings make traversal incomplete and clear returned observations.
Caller cancellation propagates, without retries. Completed source/page diagnostics
remain available, but are not resumable cursors or checkpoints.

Five sources are collected separately:

- Current open orders: bounded unpaginated response. Old last-update timestamps
  are retained because an order can remain open throughout the outage.
- Recent and historical order history: `include_old=true`, recent `include_unfilled=true`
  and historical `include_unfilled=false`,
  documented offset/count pagination to a short/empty page. The endpoint has no
  documented interval filter; all pages are scanned within the budget, then order
  updates are filtered locally by venue timestamp. Repeated included pages are
  refused; exhausting the page budget is explicit incompleteness.
- Recent and historical trades: ascending instrument sequence pagination. The first
  page uses expanded start/end timestamps; subsequent pages use `start_seq=last+1`
  with the fixed end timestamp, without combining start timestamp and start sequence.
  Sequences must progress strictly, `has_more` must be explicit, and a continuing
  empty page is refused. Equal millisecond timestamps do not skip distinct fills.

The shared WS/recovery fact parser preserves the existing v2 normalization and
canonical encoding. Recovery envelopes keep their full scope, epoch, receive time,
`RecoveryQuery` source and method provenance. Exact duplicate canonical facts are
deduplicated; conflicting immutable execution payloads block the batch. Different
order reports are retained for the existing reducer to compare. Unsupported/malformed
records, including fills without an own order ID, block rather than disappear.

## What exhaustion does and does not establish

Live API qualification established that `historical=true` with `include_unfilled=true`
is rejected with HTTP 400 / -32602 on `include_unfilled` (the venue reports these
flags cannot both be true). The connector does not retry that rejected combination.
It explicitly queries the supported historical subset, with
**`HistoricalUnfilledOrdersCovered=false` and `OrderHistoryComplete=false`** in every
result. Retained known orders need additional addressed evidence; this collector
cannot prove historical absence or recover every fully unfilled cancelled order.

`TraversalComplete` means all five bounded API enumerations of the supported subsets
exhausted without a detected problem. **`CoherentRecovery` is always false.** Recent/historical indexing
overlap, offset-page changes, mutable order snapshots and stream replay are not
fenced by these sequential REST calls. Open-order absence never manufactures a
cancellation. Historical indexing has no verified completion watermark. The result
does not produce `DeribitOrderRecoveryCoverage`, call the projector or publish facts
into the permanent inbox. A future orchestrator must establish coherence, buffered
replay, relevant retained execution ledgers and durable gates before publication.

## Separate live qualification

After a bounded Windows build, `scripts/deribit/Deribit-RecoveryCollectionLocal.ps1`
launches `--collect-recovery` with the previously saved vault and encrypted native pin.
Its default interval is the preceding day, ending five seconds before invocation;
overlap is two minutes. The only instrument is metadata-validated ETH-PERPETUAL.
The wrapper requires committed source, passes credentials through stdin, sets both
execution switches false, and saves only anonymous counts/source statuses/times and
source/assembly hashes under ignored `.tradeops/recovery-collection/`.
There is no database connection, durable write or trading harness invocation.

## Primary API references

- [Order history by instrument](https://docs.deribit.com/api-reference/trading/private-get_order_history_by_instrument)
- [User trades by instrument](https://docs.deribit.com/api-reference/trading/private-get_user_trades_by_instrument)
- [Open orders by instrument](https://docs.deribit.com/api-reference/trading/private-get_open_orders_by_instrument)
- [Recent/historical retention and delayed indexing](https://docs.deribit.com/articles/accessing-historical-trades-orders)

The published method pages were checked on 2026-11-05. Live quiet enumeration is
separate from scripted pagination/failure qualification and cannot prove fills,
account-wide coverage, coherent recovery or trading readiness.
