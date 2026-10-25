# F1-01 — VAN Deribit React parity

Source: avysotsky/alexvysotsky.com/frontend/dist/deribit.html and four deribit-*-controller.js files. Baseline is Draft PR #55 HEAD e4a067c9422845e9dd4579a36a728737f036185f. Scope: React-only presentation; no backend changes.

## Inventory and outcome

| VAN surface | React F1-01 | Missing capability |
| --- | --- | --- |
| Compact primary/trading navigation, contract strip | Typed VanNavigation, BTC/ETH perpetual quick selection | Markets, Assets, full Options and Spreads tabs disabled |
| Three-column chart/book/ticket | CSS grid: market/book/ticket, responsive below 1160px and 760px | Historical candle chart requires verified market API |
| Chart timeframe/legend | Honest public-trade microchart from current WS tape | No fabricated OHLC or backfill |
| Order book Price/Amount/Total with precision | Existing RAW/1/10/20 depth, sequence and exchange→local lag unchanged | No transport changes |
| Live public trades, balance | Existing live tape and BTC/ETH private snapshot account cards | Snapshot unknown remains unknown |
| Buy/sell ticket | Existing local ticket, explicit MAINNET disabled UI | No new API actions |
| Open orders and positions bottom dock | VanBottomTabs controlled React state; existing dense tables and font size | History, funding, exercise, assets unavailable/disabled |

VAN uses a 340px candlestick canvas, full instrument/timeframe controls, clicking book price into ticket, market/order/trade/position history and options/spreads. These are NOT yet implemented as full functional parity. No VAN backend or global mutable browser controllers copied. Chart is intentionally identified as tick-only.

## Validation commands

At pinned SHA with Node and package registry access:

    cd apps/trade-terminal
    npm install --no-audit --no-fund
    npm test
    node --test tests/vanLayout.test.mjs
    npm run build

Check RAW strict reconnect/gap handling, manual clock probe, table compactness, current UTC snapshots and MAINNET STRICT READ-ONLY on top of UI layout tests. Report any unavailable execution as NOT RUN.

## Browser screenshot acceptance (pending actual rendering)

Use existing mainnet read-only backend + React launcher. Capture 1440×900 and 390×844 at the same device scale, compare VAN reference: compact top navigation, three desktop columns; mobile stacked quote→book→ticket→public tape; depth selector and Δ exchange→local remain in same toolbar; midpoint not lost; no horizontal overflow outside local DOM/table scroll; account dock opens either Positions or Orders without fabricating zeroes. Images must come from the actual running React app, not a static mock. Do not mark visual gate PASS without saved screenshots.

## Safety

Backend DeribitTerminal__ExecutionEnabled=false and DeribitTerminal__MainnetExecutionEnabled=false must remain enforced. No trading transport, data schemas, credentials, clock correction or polling changed in this PR. History/funding, OHLC and spreads require separate typed read-only services; do not connect browser directly to private venue endpoints.

UI safeguard: the tick chart receives an empty tape whenever RAW WS is not `synced`; the navigation badge distinguishes mainnet read-only and testnet execution-gated modes.

## F1-01 interaction/accessibility follow-up
- Public trade chart supports 1m/5m/15m/All windows **relative to the latest observed trade** in the existing WebSocket buffer. This is NOT a guarantee of data coverage and never turns ticks into OHLC candles.
- `vanChartSeries.ts` validates finite positive prices/timestamps, sorts event time, caps chart to 120 trades, and handles flat/simultaneous prints without infinite/NaN coordinates. Node24 `vanLayout.test.mjs` includes actual pure-function unit tests (in addition to source assertions).
- Orders and Positions use linked `tab`/`tabpanel` IDs. Home, End, ArrowLeft and ArrowRight switch between the two currently available account panels; unavailable history/funding panels remain disabled.
- Source `deribit.html` uses a 340px canvas; React's public-tick chart is now 268px desktop / 190px mobile. Pixel-exact chart parity is NOT claimed, because equivalent OHLC API is absent. Browser comparison remains required.
