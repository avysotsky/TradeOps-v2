# TO-v2-50: exchange-side Deribit order-book subscriptions by displayed depth

## Decision: strict 10 / 25 / 50 UI choice; actual exchange capabilities

The previous terminal subscribed to `book.{instrument}.raw` at every UI depth. The 10/25/50 selector only trimmed already received data.

Current implementation sends the selected `depth` in the authenticated localhost WebSocket request, validates exactly 10/25/50 server-side, keys a separate upstream subscription by **instrument + depth**, and recreates the upstream when the React selection changes.

| UI depth | Actual Deribit WebSocket book channel | Wire data | Backend processing |
| --- | --- | --- | --- |
| 10 (default) | `book.{instrument}.none.10.100ms` | Exact 10-price-level grouped **full** snapshots, typically every 100ms | Replace entire 10-level book on every notification; no raw sequence-chain requirement |
| 25 | `book.{instrument}.raw` | Authenticated full-depth tick-by-tick book snapshot + deltas | Validate prev_change_id and apply all deltas; publish/display top 25 |
| 50 | `book.{instrument}.raw` | Authenticated full-depth tick-by-tick book snapshot + deltas | Validate prev_change_id and apply all deltas; publish/display top 50 |

**Exchange limitation:** Deribit grouped WebSocket depth subscription supports 1, 10, 20; there is no exact upstream depth 25 or 50 channel. Never subscribe to .20 and misrepresent it as 25/50, and never invent .25/.50. For 25 and 50, resubscription creates a new isolated RAW upstream even though the Deribit channel string is identical; this is not an upstream bandwidth reduction between 25/50. Future FIX API depth requests could be considered only with separately provisioned FIX market-data access.

## Lifecycle and safety

- Default React depth is 10. On depth or instrument change, React cleans up its old local socket, resets stale book/ticker/order overlay, and opens `.../market/stream?instrument={symbol}&depth={10|25|50}`.
- API rejects missing, malformed and unsupported depth with HTTP 400 and validates localhost, origin and ephemeral operator key before any lease.
- A newly selected depth owns its own `InstrumentFeed` (by instrument + depth), book state and Deribit upstream connection; it waits for a fresh initial snapshot, rather than treating the previous depth's book as valid.
- The server publishes `depth` and `bookChannel` on book/status messages. React rejects unexpected depth on these messages and reopens only with the selected depth.
- The old reader's cancellation and last-lease release stop the old Deribit feed. Existing heartbeat and separate private order overlay are unchanged.
- Raw sequence fail-closed validation remains strict. Grouped depth-10 snapshots are treated as independent full snapshots. Grouped malformed/crossed books fail closed.
- Credential source and DPAPI vault unchanged. ExecutionEnabled and MainnetExecutionEnabled hard-off in pinned launch.
- Diagnostic panel records effective Deribit channel, depth and mode; front-end labels `L2 10 · 100MS` vs `RAW L2`, rather than calling grouped mode RAW.

## Offline / owner acceptance

1. Pinned launcher builds standalone .NET API, filtered book tests, npm tests and React build **before opening DPAPI vault**.
2. Default UI depth 10: after the first subscription, inspect diagnostics. Expect `book.<instrument>.none.10.100ms`, `DEPTH_10_100MS`, ten levels max per side.
3. Switch 10 → 25: expect brief connecting/waiting_snapshot and new `book.<instrument>.raw` with RAW sequence diagnostics; see exactly 25 visible levels per side when exchange supplies them.
4. Switch 25 → 50: expect new snapshot/new lease even though the raw channel is the same; display 50 levels when available.
5. Switch 50 → 10: must unsubscribe previous local reader and start fresh grouped subscription, no stale raw book rows. Repeated switching must not flood network with overlapping subscriptions.
6. Keep diagnostics collapsed by default; record `CLIENT_WS_DISCONNECT`, counters, exchange mode, and sequence failure codes only. No account IDs/credentials/raw private messages.
7. Remain Draft until Windows owner confirms 10/25/50 switch + stable RAW behaviour. Build and runtime validation not available in assistant's container.

Reference: Deribit WebSocket L2 channel rules and distinction between raw tick-by-tick and grouped depth-limited 100ms snapshots.
