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
- PostgreSQL replay cases open fresh physical connections, reconstruct committed
  observations including quarantined alternatives, and assert unchanged legacy
  Orders/Fills. The SQL decoder is test-only, not a production reader.

[Contract and consumer requirements](../../architecture/DERIBIT_ORDER_RECOVERY_PROJECTION.md).

## Validation

Acceptance pending until actual build/TRX/cleanup/environment evidence is recorded.
The `b4-order` suite selects 247 cases: previous B3 144, journal 28, private capture
28, new reducer 45 and new PostgreSQL replay 2. Venue messages are scripted; no
keys are unlocked and no real exchange operations are performed.

## Remaining scope

This is an order-ledger reducer, not completed B4 or production trading readiness.
Next: a scoped/version/hash-validated durable reader, checkpoint/incident handling,
paginated read-only recovery collection and full positions/balances/replay coherence.
Then persist issues/projections atomically and integrate a fail-closed recovery gate.
Physical sender fencing remains B5. Existing B3 limitations and issue #73 remain.
The consumed real-order marker and retained mainnet store remain untouched.
