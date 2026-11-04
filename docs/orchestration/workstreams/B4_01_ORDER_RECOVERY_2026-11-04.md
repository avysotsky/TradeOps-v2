# B4-01 — deterministic Deribit native order recovery

After the successful one-dollar mainnet order/cancel, implement the first recovery
slice without additional live orders: rebuild native order state and the execution
ledger from committed capture plus a closed recovery batch.

## Delivered behavior

- Application-only `DeribitOrderRecoveryProjector`, explicitly Deribit-specific;
  no connector/persistence dependency, host registration, schema migration or
  execution-switch changes. Existing private facts v2 / observation v1 unchanged.
- Scope/epoch/window/coverage admission, bounded inputs, execution deduplication
  across persisted facts, WS and history, immutable-payload collision detection.
- Explicit order reports, native quantities and distinct execution totals compared
  without fabricated cancels/fills. Partial cancel preserves execution; missing
  execution history, late fills, conflicting reports, cumulative regression,
  reopening terminal orders, external orders and changed terms block consistency.
- Stable output across event arrival permutations. Unknown totals remain unknown;
  decimal overflow blocks instead of wrapping/clamping.
- Each result retains its full account scope, epoch and window. Previously observed
  native IDs are not claimed to be linked to a local OMS intent.
- PostgreSQL replay cases open fresh physical connections, reconstruct committed
  observations including quarantined alternatives, and assert unchanged legacy
  Orders/Fills. The SQL decoder is test-only, not a production reader.

[Contract and consumer requirements](../../architecture/DERIBIT_ORDER_RECOVERY_PROJECTION.md).

## Validation

The full `b4-order` run passed **247/247**, zero failures/skips, on code revision
`a1733e7eafa4427d47af5935a996c165417dfe4b`. Counts: previous B3 144,
journal 28, private capture 28, new reducer 45 and new PostgreSQL replay 2.
TRX, cleanup and environment all PASS. Normalized source hashes match that revision.
After this full run, the result contract gained account/epoch/window context and
matching assertions; the algorithm and the 200 pre-existing regression cases were
unchanged. A final targeted `b4-order-reducer` run is pending for that additive change.
Venue messages are scripted; no keys are unlocked and no real exchange operations
are performed. This is not live restart/history qualification.

Local evidence, excluded from Git:

- `.tradeops/postgres-reuse/20261104T214911-b4-order-20606/` — full 247-case run.
- `.tradeops/b4-order-full-summary.json` — verified source/binary and TRX summary.

The full-run test assembly SHA-256 is
`7ee10f15d42fe503493c3137522a7dbb4511029971fdafd4b16565dcbe6e448c`.
The reusable PG server ID/start/restart remained unchanged, test clones/roles were
cleaned and zero test sessions remained. Peer container snapshots are identical.

## Remaining scope

This is an order-ledger reducer, not completed B4 or production trading readiness.
Next: a scoped/version/hash-validated durable reader, checkpoint/incident handling,
paginated read-only recovery collection and full positions/balances/replay coherence.
Then persist issues/projections atomically and integrate a fail-closed recovery gate.
Physical sender fencing remains B5. Existing B3 limitations and issue #73 remain.
The consumed real-order marker and retained mainnet store remain untouched.
