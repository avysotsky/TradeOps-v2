# B3-01 scoped durable capture — first backend increment, 2026-11-04

Issue: [#73](https://github.com/avysotsky/TradeOps-v2/issues/73), remains **OPEN**.
Baseline: `0ba820413c6b9ac95c17419cc6765835c68628db`.
Branch: `TradeOps/b3-01-scoped-durable-capture`.
Prior step: [existing stream inventory](B3_01_PRIVATE_STREAM_INVENTORY_2026-11-04.md).

## Implemented boundary

The Application layer now exposes `IOmsPrivateEventCapture` and a versioned scoped
envelope. Infrastructure implements the durable capture transaction in
`OmsPostgresPrivateEventCapture`. This is a store-only increment: no live DI,
second stream, sender, dispatcher, legacy writes or account credential access.

Event schema 2 uses separate Deribit v2 namespaces in the existing immutable inbox.
New immutable schema-1 observations retain receipt metadata and the whole incoming
fact document, including quarantined alternatives. The additive migration leaves
old inbox v1 data/encoding, B1 version, legacy entities and global constraints intact.
It is applied only to isolated test databases in this acceptance.

Execution keys include the currency namespace, full AccountKey and the venue trade
ID/participant's own order leg. Epoch, receipt time and channel are excluded from
immutable identity/hash. Native amount/unit, prices, fee/currency, IDs and venue
timestamp are explicit; no unit conversion, invented timestamp, cumulative-fill
clamping or local aggregate mapping occurs. Decimal-scale equivalence is canonical
in v2, while a golden regression freezes v1's original bytes/hash.

Order reports use normalized content identity, not a fabricated unique revision
based on order_id/timestamp. Different facts at the same timestamp are retained;
late/out-of-order reports do not directly change an order or create a fill.

One transaction arbitrates full-scope CaptureId, inserts/compares facts, appends any
incident and commits an observation. Exact retries never report another insertion.
Reused CaptureId with different input is quarantined without admitting its unrelated
event. A conflicting execution retains the winner and the entire incoming payload;
there is no blind overwrite. Future consumers must honor observation disposition.

Details, permissions, identity policy and limitations:
[OMS_PRIVATE_EVENT_CAPTURE.md](../../architecture/OMS_PRIVATE_EVENT_CAPTURE.md).

## Actually executed

- Windows SDK 10.0.401 bounded build: **PASS**, 0 errors, 15 existing xUnit warnings.
- New capture suite: **PASS 28/28**, 0 failed/skipped. Raw TRX independently counted:
  **17 contract cases + 11 PostgreSQL integration cases**, including two theory rows
  for same/distinct concurrent CaptureIds. VSTest duration about 1m13s.
- Restricted generated application role: **PASS**. Real captures, duplicates,
  collisions and row locks work without superuser/database/role creation/replication.
  Payload/observation UPDATE denied by immutable guards; application TRUNCATE denied
  by privilege, owner TRUNCATE also denied by the new statement trigger.
- Atomicity: **PASS**. A test-only failing observation-insert trigger proves that
  a new inbox fact and a collision incident each roll back with the observation.
  Production immutable guards remain enabled throughout.
- Real fresh migrations: **PASS**. Empty Down to B2, v1 insertion, re-upgrade with
  unchanged old JSON/hash, and refusal to drop nonempty v2 evidence are executed.
- Full-scope/currency/order-leg isolation and empty legacy projections: **PASS**.
- Existing B2 compatibility suite on the same test assembly: **PASS 28/28**,
  23 PostgreSQL + 5 pure contracts, 0 failed/skipped; VSTest duration about 2m33s.
  It includes the real empty/nonempty journal migration gates, decimal replay,
  restricted application role, concurrency and dispatch/recovery metadata cases.
  Combined actually executed total: **56/56 PASS**.
- Resource Guard before/after, no clone/role/session leaks, one retained template
  and unchanged neighboring-container snapshots: **PASS** for both acceptances.
  The same dedicated server ID/start time/restart count and test-assembly SHA-256
  are retained. Owner's uncommitted launchSettings hash stays unchanged.

Local raw capture evidence: ignored
`.tradeops/postgres-reuse/20261104T113111-b3-14620/`.
Compatibility: `.tradeops/postgres-reuse/20261104T113358-b2-14642/`.
Combined independently checked TRX counters and publication evidence are recorded
in `.tradeops/b3-scoped-capture/summary.json`.

## Remaining gates

This does not complete #73. Next extend the **existing** Deribit stream/parser behind
the new port: trusted account/instrument metadata, read-only scope verification,
bounded framing/buffering, heartbeat/test_request, cancellation, reconnect/renewal,
epoch and stale-account-stream rejection. Invalid raw events require explicit
recovery handling; this port accepts normalized valid input, not arbitrary packets.

Partial historical facts with different unknown/known fields conservatively remain
quarantine candidates, not silently merged executions. The new leg-key policy and
instrument coverage still need adapter fixtures and separate live qualification.
No live self-trade, RFQ, combo or historical enrichment claim follows from this store.

Live private WS/mainnet qualification, real SIGKILL/lost-COMMIT-ACK harness, full
unrelated backend suite and browser E2E: **NOT_RUN**. Coherent recovery/projections,
verified legacy mapping, risk/physical fencing and mandatory FIX remain later gates.
No real order actions or mutating testnet qualification took place.
