# B4-03b — bounded read-only recovery collection

2026-11-05. Final implementation: `39161395cad009c57bbb192a0298ea1c3b7951d0`.
This delivers per-instrument collection with explicit coverage limitations. It does
not complete coherent recovery, account-wide coverage or B4/READY.

## Delivered

- Application query port, connector implementation with no persistence dependency,
  shared WS/recovery normalization preserving durable v2 encoding.
- Full account/epoch/manifest binding; pinned native identity checked before/after.
  Explicit short REST session with trade/account READ only and a fixed method/origin
  allowlist. No retries, sender, orders, cancellation or database access.
- Open orders, recent/historical orders and separate recent/historical fills.
  UTC outage interval plus overlap, bounded offset/sequence pagination, detection of
  stalled pages and execution collisions, byte/row/page/deadline bounds. Incomplete
  or untrusted batches return no facts. Equal timestamps do not advance a time cursor.
- Separate opt-in Windows live entry with saved vault/native pin and anonymous
  source statuses/counts/times/source/assembly hashes only.
- [Detailed contract and primary API references](../../architecture/DERIBIT_RECOVERY_QUERIES.md).

## Verified API limitation

Initial live calls authenticated and read open/recent orders, then refused historical
orders. Numeric diagnostics and a separate bounded read-only probe established:
HTTP **400**, venue **-32602**, parameter **include_unfilled**; the venue rejects
`historical=true` together with `include_unfilled=true`. No private error payload was
printed or retained. The original batch remained INCOMPLETE and returned no facts.

The final implementation explicitly enumerates the supported historical subset with
`include_unfilled=false`, retaining `include_unfilled=true` for recent orders.
**HistoricalUnfilledOrdersCovered=false, OrderHistoryComplete=false and
CoherentRecovery=false** are permanent result flags for this collector. Exhaustion
of these subsets never proves missing historical unfilled orders were cancelled or
absent. Known retained orders need addressed state evidence in the next increment.
There is also no verified historical-indexing watermark or fenced stream replay.

## Verification and evidence

Windows Release build: exit 0, zero errors, 15 existing warnings, prescribed SDK and
bounded build flags. Final `b4-collector` TRX: **137 total/executed/passed**, zero
failed/skipped; runner exit 0, environment/cleanup PASS. This includes the previous
99 reader/binding cases, 11 existing parser cases and 27 collector cases. Collector
traffic is scripted, including actual sequence/offset continuation, same-ms fills,
duplicate/collision, native account changes, invalid grants/queries, bounds, HTTP
status errors, protocol failures, cancellation and explicit history coverage holes.
All PG injection/destructive cases remain confined to owned disposable clones.

Final test evidence:
`.tradeops/postgres-reuse/20261105T093054-b4-collector-20599/`.
The 297 normalized source hashes matched the working implementation. Final test DLL
SHA256 `D644BA1444EC2FD595BA04C52C3FCC73745BC15B1C6DAC6D5A233A46E0E7EFF5`.
Earlier initial 137-case and diagnostic 38-case passes are superseded by this run.

Actual mainnet read-only enumeration of the previously pinned ETH-PERPETUAL scope:

- `RECOVERY_QUERY_TRAVERSAL_PASS`, exit 0; all five supported sources exhausted,
  one page each, with both native identity checks passing.
- Expanded window: **2026-11-04 09:32:13.458 UTC → 2026-11-05 09:34:08.480 UTC**;
  two-minute lower overlap, following the preceding-day outage query.
- Facts/order reports/executions **0/0/0**, no query problems. This is a quiet live
  enumeration of supported subsets, not live fill/pagination qualification or proof
  that the retained fully unfilled cancelled order appears in historical history.
- DatabaseUsed=false, durable writes=0, mutating calls=0; both execution switches false.
- HistoricalUnfilledOrdersCovered=false, OrderHistoryComplete=false,
  HistoricalIndexingVerified=false, AccountWideCoverage=false, TradingReady=false.
- Private summary and assembly hashes:
  `.tradeops/recovery-collection/20261105T093414-b3218457596044d59b8eb49494610fa4/`.
  Qualification DLL SHA256:
  `176B63A1B434AA9061E76561B227D34EA98123F09617A39D8E60E1A2550707BB`.

Independent retained-store reader after collection: `RETAINED_SNAPSHOT_PASS`, exit 0,
facts/observations/problems **2/2/0**. Commands/attempts **2/2**, inbox/observations/
incidents **2/2/0** unchanged; guards and container snapshots PASS. Evidence:
`.tradeops/mainnet-recovery/20261105T093428-read-snapshot-24461/`.
The completed one-order harness and its attempt marker were not invoked or rewritten.
Owner launchSettings SHA256 remains
`CBDDB50FE5C8379A82B3DD8B482696625C7F2F5DFB0058D2FD1D423653398183`.

## Next increment

B4-03c: use the retained scoped ledger to request addressed read-only evidence for
known orders missing from the supported history subsets, with explicit unsupported/
missing responses. Preserve the historical-unfilled account coverage limitation.
Then establish a coherent outage/replay cut and complete retained execution ledger,
positions/balances and durable projection/recovery gate. Physical sender fencing
remains B5. No new order is needed or authorized; issue #73 stays open.
