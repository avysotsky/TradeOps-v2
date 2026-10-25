# TO-v2-50: actual Deribit depth subscriptions, RAW default

## Exchange-supported choices

The Depth selector now exposes **1 / 10 / 20 / RAW** with **RAW as the initial selection**. There are no invented
25-level or 50-level exchange subscriptions.

| UI choice | Deribit WebSocket channel | Processing | Visible levels per side |
| --- | --- | --- | --- |
| 1 | `book.{instrument}.none.1.100ms` | Complete grouped snapshots, replace each notification | 1 |
| 10 | `book.{instrument}.none.10.100ms` | Complete grouped snapshots, replace each notification | 10 |
| 20 | `book.{instrument}.none.20.100ms` | Complete grouped snapshots, replace each notification | 20 |
| **RAW (default)** | `book.{instrument}.raw` | Initial complete book + strict `prev_change_id`-chained deltas | Display bounded to first 50; **50 is not an exchange-side depth request** |

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
2. At default RAW, diagnostic channel is `book.<instrument>.raw`.
   Validate strict change-id continuity without repeated desync.
3. Switch to 1, then 10, then 20; each change shows a fresh grouped
   channel and snapshot, and no stale rows. Repeat switches without looping.
4. Switch back to RAW; the channel must be `book.<instrument>.raw`,
   preserving strict change-id chain validation and up to 50 visible
   levels (UI cap, not exchange depth).
5. Confirm healthy updates after switching
   RAW → 1 → 10 → 20 → RAW, with no recurring websocket disconnects.
6. Keep Draft until Windows owner acceptance. Static checks do not
   prove working real-time exchange integration.

Reference: Deribit API order book group/depth/interval rules as documented
in NautilusTrader adapter guide and Deribit API.

### Exchange-to-local delta interpretation

The display `Δ exchange → local` uses `DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()`
from `DeribitRawBook.View` **after book parsing**, less the Deribit book
notification `timestamp`. This formula is unchanged from the previous
PR #51 RAW implementation. Differences like 250 ms versus 465 ms can be
caused by Windows clock offset, message transport, exchange timestamp
semantics, or local queue/processing delay. They do not establish any one
cause without an independent synchronized clock measurement. Grouped
100ms depth modes should not be compared directly with RAW for one-way
latency. If precise attribution is needed, sample Deribit's public
`/api/v2/public/get_time` server time using client before/after timestamps
and RTT; that still is only an approximation without symmetric path timing.
