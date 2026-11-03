# TO-V2-41 — Deribit RAW WebSocket market data path (first live slice)

## Why replace the prior 8-second polling

An HTTP polling loop every 8 seconds can be multiple seconds behind
the exchange during volatile markets. It is not acceptable as the
market-data source for a trading engine or a professional trading DOM.
Deribit's official market-data best practices explicitly recommend
real-time WebSocket/FIX subscriptions rather than continuous REST polling.

Official production raw L2 subscription:
  book.{instrument_name}.raw

Reference: https://docs.deribit.com/subscriptions/orderbook/bookinstrument_nameinterval

The raw channel uses Deribit's finest reported interval (1ms
aggregation) and requires authenticated WebSocket access. The very
first notification is a full book snapshot. Later messages consist of
[action,price,amount] per side; prev_change_id must equal the previous
change_id. An exchange raw feed is NOT a guarantee of end-to-end <1ms
updates when hosted on a public-internet workstation far from LD4.

## This Draft PR implements an event-driven vertical slice

Deribit WS  wss://www.deribit.com/ws/api/v2
    |
    | 1. Dedicated authenticated public/auth connection; credentials
    |    from existing read-only Windows DPAPI vault loaded into backend
    | 2. public/subscribe:
    |       book.{instrument}.raw
    |       ticker.{instrument}.raw
    |       trades.{instrument}.raw
    | 3. public/set_heartbeat (15 seconds), public/test reply
    v
C#/.NET 10 DeribitRawFeedHub (market data only)
    |
    | One source connection per actively viewed instrument; shared
    | across local browser subscribers; cap 4 simultaneously.
    | Frame receive + incremental full-depth L2 book reconstruction.
    | Snapshot seeds, change_id/prev_change_id validates continuity.
    | Invalid data or mismatch => clear L2, mark UNSYNCED, reconnect.
    | Exponential jittered reconnect, new snapshot before LIVE.
    | Internal receive loop never waits on React DOM paint.
    |
    +----> eventual broker-independent trading event bus
    |          NOT YET CONNECTED (no trading on this PR)
    |
    v
Local ASP.NET WebSocket endpoint
  ws://127.0.0.1:5058/api/deribit-terminal/market/stream
  - Loopback only + Vite origin match + ephemeral operator key
  - Up to 4 instrument feeds, bounded latest-state per browser queue
  - Browser backpressure cannot block raw source book processing
    |
    v
Vite localhost WebSocket proxy (127.0.0.1:5173)
  - Server-only random session key, injected into WebSocket upgrade
  - React/browser never receives operator key or exchange credentials
    |
    v
React browser
  - One event-driven WebSocket for selected public market instrument
  - Last 10 bid/ask levels from full-depth L2, recent raw trade stream,
    quote and sequence state, exchange timestamp age estimate
  - requestAnimationFrame coalescing only for VISUAL updates; render
    throughput is limited by browser display refresh, while the C#
    source processes ALL raw deltas and validates sequence
  - A red disconnect / waiting snapshot / resync status hides outdated
    book. A green LIVE RAW badge appears only after L2 is valid.
  - Read-only private account/positions polling at 8 seconds stays
    separate: it does not control the DOM or trading market data.

## Risk boundaries and truthful performance limits

This is an engineering prototype for the correct architecture,
**not yet an independently measured or owner-accepted production feed**.
No actual exchange WebSocket event, .NET build, React build or user
latency result has been observed for the new SHA. Do NOT claim
sub-millisecond performance as a verified result.

Further hardening required before institutional live execution:
- Persistent market-data collector independent of any browser tab, with
  configured instruments used by strategies even if React is closed.
- Isolate feed and execution into separate Deribit sockets and bounded,
  dedicated low-latency processes/services, with no DB/React in the
  actual order decision path.
- End-to-end latency instrumentation: exchange timestamp vs local
  receive wall time (NTP/PTP required), parse-to-book (Stopwatch),
  book-to-trading engine and book-to-browser delivery, render age.
  Measure p50/p95/p99/p99.9 in normal/volatile traffic, including
  backlog, gaps, reconnect, stale periods and sustained throughput.
- Deterministic replay testing of raw book messages under loss,
  duplicate, reorder, malformed JSON and long bursts; one sole
  sequential event processor for each instrument, no silent data loss
  before the core book state.
- Full raw snapshot can be large; optimize allocations further using
  pooled parsers and benchmark the CPU/GC profile under real traffic.
- Route production service to London/LD4-adjacent hosting if network
  measurements show material WAN latency. Deribit says standard
  WebSocket and FIX have similar market-data speed, while lowest
  latency restricted-access **Starbase SBE multicast** requires
  suitable private connectivity/colocation. Do not build for the
  sunset legacy multicast product as new architecture.

## Safety

Both ExecuteEnabled (ExecutionEnabled) and
MainnetExecutionEnabled remain hard-set false by the Windows launcher.
No buy, sell, order cancellation or money movement is performed.
Authenticated RAW subscriptions are MARKET DATA only; existing
read-only mainnet exchange credentials are not upgraded/reused as
trading-permission keys. Operator session token is not copied into
browser JS. Existing trade order ticket stays blocked and unchanged.

## Required owner gate

1. Stop old mainnet read-only terminal with Ctrl+C.
2. Execute the pinned new SHA of this branch through the existing
   one-command PowerShell bootstrap (no password prompts).
3. Confirm .NET 10 and React builds PASS and local proxy status PASS.
4. In React confirm LIVE RAW / non-empty ETH-PERPETUAL bids+asks,
   exchangeAgeMs gauge and sequence number increasing with market
   events; repeat with a dated future and/or option.
5. Cut or pause network briefly, observe DISCONNECTED/RESYNCING and
   cleared stale book; reconnect must obtain new snapshot and display
   LIVE RAW again. No accidental broker order paths.
6. Collect latency p50/p99 separately before making any SLA claims.

Docs:
https://docs.deribit.com/articles/market-data-collection-best-practices
https://docs.deribit.com/articles/connection-management-best-practices
https://docs.deribit.com/subscriptions/orderbook/bookinstrument_nameinterval
