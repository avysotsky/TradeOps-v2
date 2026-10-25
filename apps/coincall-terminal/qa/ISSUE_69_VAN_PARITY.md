# F2-VIS — CoinCall VAN to React browser parity (Issue #69)

## Source identity and safety mode

- VAN source repository: `avysotsky/alexvysotsky.com`, pinned commit `702a6f51006be4e9c35c65d21ec7f85c40c5a2d4`.
- Actual source files examined: `frontend/dist/coincall.html`, `frontend/dist/coincall-spreads.html`, and `frontend/dist/assets/coincall-spreads-full.js`, with inline CSS from the first two.
- Target: `apps/coincall-terminal/`, Draft PR #64. No backend, other workstation, repository root or GitHub Actions changes.
- VAN **reference-browser safety**: the runner serves the original **HTML and inline CSS** at the pinned Git commit, strips **all executable script tags** and embedded iframes, blocks remote network, and sets any order/admin action buttons disabled. It does **not** run legacy authenticated VAN JavaScript (not even if credentials are present). Static first-paint spread matrix from the original `coincall-spreads.html` is retained. For empty static spot/futures/account views, the runner inserts visibly labelled *synthetic DOM-only fixtures* (not authenticated VAN data).
- Target-browser safety: compiles and runs the actual F2 React/Vite bundle; all outbound HTTP(s)/WS denied and recorded; trading disabled.
- **Meaning**: real Chromium layout comparison of source-derived HTML/CSS to compiled React, **not** functional certification of VAN JS, exact bid/ask number equality, trading parity or pixel-perfect equivalence. The source static matrix contains June–March 2026/27 historic rows; React uses the deterministic October–November 2026 fixtures. Compare geometry and workflows rather than literal values.
- Local browser/WSL2/Docker execution status in this chat: **NOT RUN**. No fabricated screenshots, logs or PASS. The runner is included for local execution at a pinned PR HEAD.

## Source-grounded feature matrix

| Feature | VAN implementation evidence | F2 React representation | Parity status before screenshots |
| --- | --- | --- | --- |
| Layout / navigation | Portal venue navbar plus Markets / Trade / Assets / Accounts and Spot / Futures / Options / Data / Spreads | Standalone venue workstation, local tabs; no global multi-venue nav | PARTIAL |
| Spreads grid | `#coincallSpreadsGridHost .okx-nitro-grid`: sticky two-axis row/column matrix, contract headers, price columns, bid/ask quote cells | **Corrected** Grid is actual 2D instrument matrix with sticky row/column, signed synthetic quotes; Table is a distinct alternate view | PARTIAL (baseline has more expiries, 2 price columns and denser cells) |
| Spreads pricing modes | `#ccSpreadsDisplaySwitch` supports $, $ %, %; `#ccSpreadsPriceSourceSwitch` MarkPrice/BidAskMedian | All three display states plus mark/median mock switch; deterministic fixtures, not venue quotes | PARTIAL |
| Expiry filters | Distinct DailyExp, WeeklyExp, MonthlyExp, QuarterlyExp; weekly excludes last monthly/quarterly Friday | Fixed classification: Oct 30 and Nov 27 = Monthly, Nov 06 = Weekly; no daily/quarterly fixture records | PARTIAL |
| Spread two legs | Original VAN JS includes modal trading panels with two legs, side books, positions/history and trade controls | Click a matrix cell to display BUY/SELL legs, basis/funding; disabled Buy/Sell spread buttons; no two-books modal | PARTIAL |
| Spot instruments | `#ccSpotInst` lists BTCUSDT, ETHUSDT, SOLUSDT | Mock BTC/USDT, ETH/USDT, **SOL/USDT** selection | PARTIAL (fixtures only) |
| Spot book + ticket | `#ccSpotAsks/#ccSpotBids`, chart, Limit/Market, post-only, price, size, Buy/Sell | 11 levels each side, book→ticket price preview, Limit/Market/post-only, inert Buy/Sell | PARTIAL |
| Futures book + ticket | `#ccFuturesAsks/#ccFuturesBids`, precision select, contract strip, cross/isolated leverage, order size unit, Tools | Synthetic 22-level book, tick/step, margin/leverage/size, inert Long/Short; precision control, contract strip, unit toggles, tools still absent | PARTIAL |
| Positions/orders tabs | Open orders/positions plus history, funding, assets etc, tabbed panels | Synthetic open orders/positions tables scoped by account; several history tabs absent | PARTIAL |
| Account selection and keys | `#ccAccountSelect`, Set active, Delete, Test credentials, Add account API form | Two synthetic accounts with strict mock AccountKey and balances/orders/positions isolation; **no credentials deliberately** | PARTIAL / intentional omission |
| Portfolio/PnL | Estimated total, trading balances, detailed equity charts, funding and asset analytics | Equity/balances/open orders, PnL/Funding/History some placeholders | PARTIAL / historical analytics MISSING |
| Options | Expiries, chain and instrument detail; VAN JS can load live chain | Mock options chain without Greeks or execution | PARTIAL |
| Actual live trading / authenticated browser API | Legacy direct browser API and realtime WS exist in VAN `coincall-spreads-full.js` | Intentionally NO TRANSPORT or execution pending backend OMS safety gates | MISSING by design |

## Image / log evidence expected after local gate

The pinned local script `qa/accept-local.sh <PR_SHA>` must produce `$HOME/TradeOps-acceptance/F2-01/<PR_SHA>/` with:

- `identity.log`, `worktree.log`, `build-tests.log`, `browser.log`, `browser-check.json`, `status.txt`, `van-fetch.log`, `van-parity.log`, `van-parity-status.txt`.
- `ui/coincall-react-1440x900.png`, `ui/coincall-react-768x900.png` from compiled React basic smoke.
- `van-ui/van-spreads-1440x900.png`, `van-ui/van-spreads-768x900.png`, plus `van-spread-legs-<viewport>.png` (original VAN modal HTML/CSS shell with **injected disabled synthetic legs**, not a live VAN ticket); and similarly `van-spot`, `van-futures`, `van-portfolio`, `van-accounts`.
- `van-ui/react-spreads-grid-...`, `react-spreads-legs-...`, `react-spot-...`, `react-futures-...`, `react-portfolio-...`, `react-accounts-...`, `react-orders-account-beta-...`.
- `van-ui/parity-manifest.json`, `van-ui/geometry-1440x900.json`, `van-ui/geometry-768x900.json` with exact SHA provenance, viewport and PASS/FAIL for DOM smoke.

A browser PASS means screenshots were **captured and UI smoke assertions passed**. Actual VISUAL parity must still be separately reviewed side by side, recording discrepancies and accepted/remediated deltas. No screenshot or numerical visual-diff claim before these PNGs exist.

## Known visual/function gaps requiring orchestrator decision

1. Entire portal-level multi-venue navbar is intentionally absent from the isolated terminal; F3 may supply a higher-level shell.
2. VAN grid includes Index/MarkPrice and BidAskMedian **per row**, six or more expiries and very dense 30px rows; React still lacks these row price fields and has synthetic fewer maturities.
3. VAN two-leg spread trade interaction uses full-size modal with independent books, per-leg ticket and related positions/trade history; React currently has only a side-by-side two-leg preview plus inert controls.
4. Futures precision options, independent leverage chips / trade tools, order-history and funding panels are not modeled. Account credentials are deliberately excluded for security.
5. Full VAN portfolio analytics, options Greeks/historical PnL and authenticated runtime are absent. Never substitute fictional data for them without explicit approval.

**Current verdict:** implemented significant structure corrections; **visual acceptance NOT RUN** until real screenshot artifacts from exact HEAD and pinned VAN source are captured and reviewed. GitHub Actions forbidden. MAINNET READ-ONLY; no merge.

## Gate counts

- Node24 TAP suite: **9 test declarations** (model, calendar sorting, SOL spot, account isolation, source safety); NOT RUN for this HEAD.
- React Chromium smoke: **18 checks**, 9 per viewport; NOT RUN.
- VAN HTML/CSS versus React capture: source-pinned separate Chromium contexts, mode-specific captures, geometry and JSON manifest; NOT RUN.
- Visual approval requires a human/orchestrator to review actual PNGs; no pixel-equivalence claim is made from static source inspection.
