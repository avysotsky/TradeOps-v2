# TO-V2-47 — RAW L2 depth 10 / 25 / 50 and off-range own orders

## Owner-accepted baseline

Owner confirms real Deribit mainnet own-order markers display correctly
in PR #49, frozen SHA 72aac178f918c8381402320e40c0f3747ae635b8.
The accepted ultra-compact 18px vertical Price / Amount / Cumulative
DOM, Last Trade / Mark / Index row, specification-based decimal places,
read-only authentication and RAW WebSocket remain unchanged.

## Operator features

- An inline selector above the DOM chooses **10, 25 or 50** depth levels
  PER SIDE (bid and ask). Default remains **10**.
- The .NET RAW book engine has always reconstructed the FULL L2 feed;
  this increment serializes a maximum **50** top levels per side instead
  of 10, without opening more exchange connections, adding REST polling,
  trading permissions or altering exchange event sequence validation.
- React selects and renders only the requested number of levels,
  keeping ask descending to the reference price band, bid descending
  away from it, own-order markers and cumulative public volume.
- A 460px scrollable DOM viewport keeps the best quotes and Last /
  Mark / Index around the center when switching to 25/50. React scroll
  position is not reset on each market delta; no extra book copy or
  network request is triggered when switching DOM depth.
- An account-private **Мои заявки вне N уровней** expandable control
  is shown only when the own-order snapshot is READY, the underlying
  RAW book is SYNCED, and the selected symbol owns open resting
  orders beyond the displayed range. It lists ACTUAL own direction,
  price, remaining own size and count by exact level. It NEVER invents
  public L2 volume at a price outside received depth.
- The own-order list updates through the existing read-only private
  WebSocket channel; no order details are posted to external apps.
- Offscreen own orders with the wrong side at a displayed public price
  are not glued to that price as if the order rested there.
- Price precision is still based exclusively on selected Deribit
  contract tick_size/tick_size_steps (with fail-closed missing spec).
- Own and public volume remain distinct; own amount may already be
  included in public L2 and is never added a second time.

## Safety and latency boundaries

- No change to the Deribit upstream RAW WebSocket, full-depth L2
  reconstruction correctness model, sequence chain checking,
  connection management or private order event reconciliation.
- To support max 50, browser-facing book snapshots now contain up to
  100 price/size pairs per event, instead of 20. This increases
  serialization/network cost. The source was optimized to avoid an
  extra 50-level array copy per valid delta; however **the actual CPU,
  GC and p50/p99 performance impact has NOT BEEN MEASURED**.
  Do not claim institutional latency SLA until benchmarked under
  volatile market bursts.
- React visual updates are coalesced via requestAnimationFrame only;
  market book is built at full source event rate in .NET.
- No new buy, sell, cancel, edit, preview or background execution
  routes. Production execution stays strictly disabled with existing
  Windows DPAPI read-only vault, ephemeral password-free operator
  auth and the existing single PowerShell pinned launch script.
- CI tests for 75 synthetic full-book levels, 50-level cap, replacement
  depth after deleting top-ten levels, current 10 default, 25/50
  visible top depth, exact side/price offscreen own classification,
  no public size fabrication, disabled live trading and React build.
- This branch is a Draft. No actual user-PC compile, exchange run,
  screenshot acceptance or p99 measurement observed yet.

## Owner run acceptance

1. Stop previous PowerShell via Ctrl+C.
2. Launch this Draft branch by its exact verified SHA, allowing offline
   Node/.NET tests and builds to run before vault access.
3. Confirm original LIVE RAW, own markers and 18px DOM density.
4. Select 25, then 50. View area stays within ~460px; best bid/ask
   remain adjacent to reference Last/Mark/Index row after recenter.
5. If an own order rests outside top 10 but within top 25/50, marker
   should appear at exact side and price when chosen depth includes
   that level. If still beyond 50, it remains visible in separate
   expandable own-order list without a fake public volume.
6. Confirm no clickable trade controls added and live mainnet order
   actions remain disabled. Do not open new real orders for testing.
7. Compare first-received-event and p99 browser/book timings with
   predecessor before increasing max depth beyond 50.

## Possible next phase

- Persistent streaming service independent of React with book
  10/25/50 views encoded per-subscriber to avoid serializing 50 when
  only a 10-depth browser is watching.
- Event capture & deterministic replay, p50/p95/p99.9 parse and
  browser delivery latency counters, queue drops and backpressure.
- Trading order/fill reconciliation with isolated private order
  connection, separate from public market data.
