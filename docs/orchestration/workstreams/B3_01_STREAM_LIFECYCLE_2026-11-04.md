# B3-01 read-only stream lifecycle — 2026-11-04

Issue: [#73](https://github.com/avysotsky/TradeOps-v2/issues/73), remains **OPEN**.
Baseline: `68a0cac16db8f62d5f2a93c3600ef84588f12473`.
Branch: `TradeOps/b3-01-readonly-stream-lifecycle`.
Preceding step: [scoped durable capture](B3_01_SCOPED_CAPTURE_2026-11-04.md).

## Implemented

The existing Deribit stream/parser now have an explicit read-only capture path into
the application coordinator and durable capture port. It validates endpoint against
the full AccountKey, requests/verifies read-only scopes, checks extended remote
account identity against trusted server configuration, and verifies all requested
subscription ACK channels before releasing buffered events.

One reader handles handshake events, correlated RPC replies and heartbeat/test_request.
A bounded queue and separate capture writer preserve reader liveness during DB waits.
Message/event limits, strict native normalization, deadlines, cancellation, reconnect,
planned token renewal and stream epochs are implemented. A commit barrier prevents
old account/epoch work from committing after the transition or changing current status.
Duplicate runners/legacy+capture overlap on the same instance are refused.

All connected epochs remain RECOVERING. No READY, read model, Order/Fill write,
sender/dispatcher, host enablement, credentials or live registration is added.
Detailed protocol/resource/coverage behavior and remaining boundaries:
[DERIBIT_PRIVATE_CAPTURE_LIFECYCLE.md](../../architecture/DERIBIT_PRIVATE_CAPTURE_LIFECYCLE.md).

## Actually executed

Final local acceptance: **52/52 PASS**, **0 failed/skipped**, raw TRX independently checked.

| Executed group | Cases | Evidence scope |
|---|---:|---|
| New lifecycle/RPC tests | 20 | Read-only scopes, pinned account, endpoints, handshake buffering, heartbeat under slow capture, overflow, reconnect, renewal, silence, account switch, runner exclusion, sanitized failures |
| New normalized-parser cases | 11 | Futures/linear/options native units, unknown instrument, invalid records and cancel partial-fill preservation |
| Application coordinator | 3 | Commit/switch barrier, stale epoch/scope rejection, session ownership |
| Real loopback WebSocket | 5 | Fragmented UTF-8, size/binary/invalid-UTF-8 rejection, cancellation; no Deribit endpoint |
| Real PostgreSQL pipeline | 1 | Scripted RPC stream → normalizer → coordinator → actual PG17 with a restricted app login; reconnect gives one inbox fact and two observations |
| Existing Deribit contracts | 12 | Adapter/config/parser and two Deribit-specific registration tests |

The PostgreSQL pipeline observes the committed first event before injecting disconnect,
then checks Duplicate after the second epoch, distinct epochs, full account scope,
non-superuser role and no legacy Orders/Fills/commands/outbox/aggregate writes.
The writer uses the existing v2 fact/observation encoding; no schema migration changed.

- Windows bounded .NET build: **PASS**, 0 errors, 15 pre-existing xUnit warnings.
- Resource Guard before/after: **PASS**; existing-container ID/start time/restart count,
  status/health snapshots unchanged. Persistent infrastructure is not used.
- Isolated PG limits unchanged: 384 MiB / 0.5 CPU, test process one CPU. Retained server
  and schema template reused; no clone/role/test-database-session leaks.
- Owner's uncommitted launchSettings hash unchanged.
- Initial run: **51 PASS / 1 FAIL**. The UTF-8 test expected wrapper rejection but
  ManagedWebSocket correctly rejected it earlier with WebSocketException. The test
  now accepts either explicit protocol rejection layer; timeout/arbitrary exceptions
  and successful delivery still fail it. No production behavior was weakened.
- Full unrelated backend/store reruns, browser E2E, live Deribit/mainnet private WS,
  real SIGKILL/lost-COMMIT-ACK and mutating testnet checks: **NOT_RUN**.

Raw evidence: ignored `.tradeops/postgres-reuse/20261104T121845-b3-stream-4913/`.
The failed development run remains in `20261104T121629-b3-stream-11053/`.
Combined counters/source binding: `.tradeops/b3-stream-lifecycle/summary.json`.
Acceptance records normalized source-file SHA-256 manifests and the actual test
assembly hash; source content is compared to the published commit independently.

## Next gate

Configure a trusted server-owned credential/account/instrument manifest binding and
exclusive host composition, then perform separate **live read-only** acceptance.
Scopes, account identity, channel eligibility/coverage, reconnect behavior and actual
capture must be confirmed against Deribit. No credential vault was read in this step.
Do not enable both the legacy worker and the new entry point for the same adapter.

B4 must still provide coherent snapshots/history/fills and authoritative projections,
including queued/unobserved gaps. Gap/phase metadata here is process-local; restart
always starts blocked for recovery. Existing legacy mapping, B5 risk/fencing and
mandatory Deribit FIX 4.4 through QuickFIX/n remain later gates. No trading readiness
is claimed and no real order actions occurred.
