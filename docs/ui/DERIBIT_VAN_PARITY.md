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

## Issue #68 — actual browser evidence gate (no fabricated captures)

The existing CSS was compared with VAN `frontend/dist/deribit.html`: VAN's futures shell uses a `1280px` maximum, `420+ / 280 / 300px` columns, `8px` gutters and `418px` futures-card height. React F1 now limits its desktop workbench to `1280px` with `420+ / 312 / 300px` columns. The 32px larger React book is intentional: preserving its existing `105+88+102px` RAW ladder width is more important than hiding price/size columns. This is not pixel-perfect parity.

**Browser automation source:** `apps/trade-terminal/tools/f1-browser-compare.mjs`. It takes two existing **local-only** app URLs, exact PR SHA and output folder, runs Playwright Chromium at `1440×900` and `390×844`, screenshots the workstation and each available Orders/Positions panel, captures DOM row sizes, overflow, RAW/delta indications, and emits `browser-metrics.json` plus `parity-browser-report.md`. It blocks external requests and does not call trading APIs; the backend must still be explicitly read-only.

Prerequisites: owner local WSL2/Windows or isolated Node24 runner, **both apps locally served** (VAN at localhost, TradeOps React at localhost with local operator launcher), Chromium/Playwright installed, sanitized fixtures or public-only data; credentials must be absent from screenshots. For local VAN static serving, authenticate no real accounts. If VAN static HTML cannot load its own data, do NOT pretend the visual comparison covers live data. Use exact same market instrument and comparable fixtures; VAN's *precision* selector is not equivalent to RAW depth.

Example from `apps/trade-terminal` after `npm install --no-save --no-package-lock playwright`:

```bash
node tools/f1-browser-compare.mjs \
  --van http://127.0.0.1:8766/deribit.html \
  --react http://127.0.0.1:5173/ \
  --sha "$(git rev-parse HEAD)" \
  --out "$HOME/tradeops-acceptance-results/f1-68/$(git rev-parse HEAD)"
```

Run **separately** from CI-01's exact-SHA Node24 `npm test`, `node --test tests/vanLayout.test.mjs`, `npm run build` runner. Complete evidence requires SHA verified before and after, test counts, compiler output, fixture provenance, screenshot hashes, browser errors, 1440×900 + 390×844 images **from each actual app**, and a human-reviewed variance list. Screenshot capture does not establish functional parity by itself. Do not merge until orchestrator verifies these artifacts.

**As of authoring**, owner WSL2/Docker execution, browser against both actual apps and screenshots are `NOT RUN`; do not equate this script or source inspection with browser PASS. GitHub Actions forbidden. MAINNET STRICT READ-ONLY.
