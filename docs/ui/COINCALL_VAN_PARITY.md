# F2-01 CoinCall VAN UI inventory and parity
Reference repository: `avysotsky/alexvysotsky.com` (main). Delivery in NEW `apps/coincall-terminal/**` only. Issue #60; Master Roadmap Draft PR #56.

## Source-exact inventory
| VAN source | Observed | React F2-01 |
| --- | --- | --- |
| `frontend/dist/coincall.html` (~953 KB) | Shared VAN Portal navigation; Markets, Trade, Assets, Accounts; header Equity/IM%/MM%/margin mode/Available/account. Product tabs Spot, Futures, Options, Data, Spreads; default Spreads. Spot/futures chart, order book, price/amount/order type, post-only, leverage, balances and open order/position table | Matches structural sections, compact header, synthetic chart/book/ticket, typed mock data; order buttons disabled |
| `frontend/dist/coincall-spreads.html` (~200 KB) | Spreads-first standalone page with BTC/ETH, USDT-M/Crypto-M, Grid/Table, Diff/Bid/Ask and expiry filters, MarkPrice/BidAskMedian switches; real quote matrix and optional chart | Default spreads view. Synthetic quote matrix, filters, price modes, two-leg detail panel and mock chart; trading inert |
| `frontend/dist/assets/coincall-spreads-full.js` (~848 KB) | Client DOM mutations and direct API helpers, private websocket and real-time trading, positions/funding, orders/leg actions | **Not copied.** Typed React state only; no authenticated CoinCall REST/WS, no executable trading |
| `tests/orders_net_regression.js`, `tests/test_spreadbot_orders_net_dom.py` | VAN spread bot Orders Net automation, levels, USD spacing and order lifecycle regression. DOM tests reference a later spread-workstation path | These execution-specific tests cannot be reused in read-only shell. New unit tests cover account isolation and spread leg ordering; no Orders Net implementation |
| `coincall-spread-workstation.html` | Roadmap flags placeholder, not an authoritative functioning workstation source | Excluded |

## Visual and interaction design
VAN CSS exhibits Segoe UI/system, dark `#050b16` / `#0b1220`, 1px `#1e293b` borders and compact desktop 11–13px typography. Observed spot 3-column composition chart / ~280px book / ~300px ticket and ~418px card heights; React keeps 277px and 285px columns at desktop and 440px book depth with breakpoint to 2/1 columns. Spreads includes filters, USD/% switch, mark/median selector, row-click leg preview. The selected account changes rows in both orders and positions as well as account balances; no cross-account mixing.

## Implementation truth
Fully implemented *mock UX*: tab state, typed accounts, deterministic market/book/spread fixtures, buy/sell leg display, table filters, selected quote detail, order ticket preview, disabled action controls, responsive layouts. Only represented or deferred: live market data, historical PnL, Greeks, funding history, options execution, API key management, positions reconciliation, real OMS, multi-leg saga, Orders Net bot, full VAN pixel fidelity, live risk and actual order entry. `Grid/Table` and USDT/Crypto-M are UI selection states over the **same** fixture set, not separate live exchange books; expiry filters without fixtures present empty state.

## Acceptance evidence and gaps
- Unit tests: `node --experimental-strip-types --test tests/*.test.mts`; deterministic account isolation, buy/sell leg types, sorted mock book levels, quote source switch.
- Build command: `npm run build` (TypeScript 5 + Vite 6). If dependencies are unavailable during review, report **NOT RUN**, not success.
- Browser capture targets: 1440×900 desktop and 768×900 narrow. Capture **actual React bundle**, not an unrelated static mock. Report **NOT RUN** if screenshot environment is unavailable; no fabricated golden comparison.
- Strict safety gate: no authenticated API calls, all order actions HTML-disabled, credentials absent, no backend or Deribit changes.
- Shared component proposals for orchestrator / F1 / F3: compact account badge, token palette, L2 book, tab navigation, typed order/position table. Not extracted into shared paths to preserve worker isolation.
- Remaining parity gap: screenshot baselines against exact VAN browser output, historical tabs, options Greeks, full spreads row types and source-specific precision, keyboard/e2e accessibility, and broker-certified read models. These require follow-on issue approvals. No merge before G0+orchestrator review.

## F2-01 fixes and acceptance plan (2026-10-25)

Original defect: WeeklyExp and MonthlyExp controls matched their display labels against literal tenor strings 30 Oct / 27 Nov / Perp; therefore selecting them yielded no matches. Typed SpreadQuote.expiryGroup now classifies 30 Oct = Weekly, 27 Nov = Monthly and Perp = Perpetual. Daily/Quarterly show explicit empty fixture results. No live exchange expiry classification is implied.

Book-row selection now also populates the inert order-ticket price field (no order submission). Unit-test declarations increased from 4 to 5 with regression coverage for expiry filtering.

Exact-SHA runner: apps/coincall-terminal/qa/accept-local.sh <SHA>. It uses local WSL2 Docker node:24-bookworm and Playwright Chromium with the built Vite bundle. Refuses a mismatched PR HEAD. Preserves identity.log, worktree.log, build-tests.log, browser.log, browser-check.json, screenshots at 1440x900 and 768x900, plus status.txt. Browser runner executes 14 named assertions (7 at each viewport).

Manual reference parity: capture VAN CoinCall Spreads with matching viewport, account/navigation and source revision; compare header and product navigation, spread matrix rows/columns, chart and leg split, order-ticket and book footprint, font/spacing/colors, and narrow scrolling. Record screenshot provenance, discrepancy list and explicit PASS/FAIL. React-only screenshot evidence is not VAN parity.

Current new-HEAD unit/build/browser results: NOT RUN in this chat's container, which cannot access user WSL2/Docker. Reference VAN screenshots: NOT RUN. GitHub Actions are prohibited, not a blocker. No merge before independent orchestration review.
