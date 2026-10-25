# TO-v2-50: actual Deribit depth subscriptions, default 10

## Exchange-supported choices

The Depth selector now exposes **1 / 10 / 20 / RAW**. There are no invented
25-level or 50-level exchange subscriptions.

| UI choice | Deribit WebSocket channel | Processing | Visible levels per side |
| --- | --- | --- | --- |
| 1 | `book.{instrument}.none.1.100ms` | Complete grouped snapshots, replace each notification | 1 |
| **10 (default)** | `book.{instrument}.none.10.100ms` | Complete grouped snapshots, replace each notification | 10 |
| 20 | `book.{instrument}.none.20.100ms` | Complete grouped snapshots, replace each notification | 20 |
| RAW | `book.{instrument}.raw` | Initial complete book + strict `prev_change_id`-chained deltas | Display bounded to first 50; **50 is not an exchange-side depth request** |

Deribit grouped channels offer exactly 1, 10, and 20 levels per side.
RAW is a separate full-depth, authenticated, tick-by-tick feed.

### Lifecycle

- React sends `?instrument=...&depth=1|10|20|raw`. The server requires
  one of these four strings and returns HTTP 400 for all others
  (including 0, 25 and 50).
- A depth/instrument switch explicitly closes the old local WebSocket,
  clears stale book/own-order overlay/ticker/tape, then opens a new one.
- Upstream leases are keyed by the instrument and internal selected mode.
  An old snapshot cannot seed a different-depth view.
- Exchange channel metadata and mode are emitted in read-only diagnostics.
- Grouped messages replace an entire bounded image; any invalid/crossed
  snapshot fails closed. RAW deltas retain strict sequence-gap detection.
- All book channels share the pre-existing heartbeat, read-only private
  order overlay, bounded browser latest-state fan-out, and safety gates.

### Read-only safety

Launcher continues to enforce
`DeribitTerminal__ExecutionEnabled=false` and
`DeribitTerminal__MainnetExecutionEnabled=false` on MAINNET.
The DPAPI vault is never decrypted before .NET and React builds/tests pass.
No credential or private order payload is exposed by diagnostics.

### Regression / owner acceptance

1. Pinned SHA launcher builds standalone TerminalApi, xUnit filtered
   `DeribitRawBookTests`, node tests, and React.
2. At default 10, diagnostic channel is
   `book.<instrument>.none.10.100ms`.
3. Switch to 1, then 20; each change shows a fresh grouped channel, new
   snapshot, and no stale rows. Repeated changes must not loop in resync.
4. Switch to RAW; diagnostic channel must be
   `book.<instrument>.raw`, change_id chain validation remains active,
   and up to 50 levels are displayed without calling this a
   50-level subscription.
5. Return to 10. Confirm ongoing healthy updates after switching
   10 → 1 → 20 → RAW → 10, with no recurring websocket disconnects.
6. Keep Draft until Windows owner acceptance. Static checks do not
   prove working real-time exchange integration.

Reference: Deribit API order book group/depth/interval rules as documented
in NautilusTrader adapter guide and Deribit API.
