# F2-01 — Independent CoinCall React Workstation
Stand-alone React 19, TypeScript 5, Vite 6 application. Synthetic fixtures only; no account credentials, browser persistence, backend dependencies, authenticated CoinCall API, Deribit integration or order submission.

## Run
```sh
cd apps/coincall-terminal
npm install
npm run check
npm run dev
```
Open http://127.0.0.1:5181. `npm run test` uses Node's built-in TypeScript strip-types runner (Node 22+); `npm run build` runs strict TypeScript and Vite.

## Scope
- VAN-inspired Markets / Trade / Assets / Accounts navigation, header equity/IM/MM/margin/account.
- Spot (BTC/ETH/SOL) and Futures (BTC/ETH): synthetic mark/price/funding, responsive chart / book / ticket, 22 book levels, disabled Buy/Sell.
- Options: representative mock chain. Data: local mock chart.
- Spreads default: BTC/ETH, margin type, Grid/Table selection, Bid/Ask/Diff and expiry filters, USD/% and MarkPrice/BidAskMedian. Click quote for accessible mock two-leg modal, two independent L2-style books, account-scoped open orders/positions and disabled two-leg execution controls.
- Assets: account-scoped balances, orders, positions, funding, explicit nonimplemented analytics.
- Accounts: two mock account contexts; no credentials or API key form.
All market/book/order/position records are deterministic snapshot fixtures `2026-10-25T12:00:00Z`, not live trading information.

## Safety
No `fetch`, `WebSocket`, network/client SDK, localStorage, execution endpoint, imported VAN JS, order POST or authenticated credentials. Trading buttons have `disabled` HTML attributes. No claimed bot / orders-net parity.

G0/orchestrator review required; no merge. See `docs/ui/COINCALL_VAN_PARITY.md`.

## WSL2 / Docker acceptance on exact PR HEAD

Run from a WSL2 Ubuntu checkout with Docker enabled:

    bash apps/coincall-terminal/qa/accept-local.sh CURRENT_40_CHAR_PR_HEAD_SHA

If your checkout is on main or another worker branch, do not switch branches. From the TradeOps-v2 checkout root instead run:

    git fetch origin refs/heads/TradeOps/f2-01-coincall-react-workstation
    git show FETCH_HEAD:apps/coincall-terminal/qa/accept-local.sh | bash -s -- CURRENT_40_CHAR_PR_HEAD_SHA

The script fetches the F2-01 PR branch, fails on any remote SHA mismatch, creates an isolated detached worktree, runs unit tests and strict production TS/Vite build in node:24-bookworm, and then serves compiled assets for Chromium smoke checks inside mcr.microsoft.com/playwright:v1.56.1-noble.

The 20 browser checks (10 per viewport) cover the mock spread matrix and its Weekly/Monthly filters, inert two-leg buttons and Spot/Futures ticket actions, mock order-book price input, account isolation, and absence of outbound requests and browser exceptions at 1440x900 and 768x900. Results, TAP output, SHA verification, Node version, npm dependency list, React/VAN PNG screenshots, per-viewport geometry JSON, parity-manifest.json, and browser-check.json are saved under $HOME/TradeOps-acceptance/F2-01/<SHA>/.

The runner also captures the original source-pinned VAN HTML/CSS at both viewports with its JavaScript/inline event handlers disabled, and saves a screen-by-screen geometry/DOM manifest. React-versus-VAN screenshot capture success is not pixel-parity approval; the orchestrator must inspect and sign off on visible differences. No GitHub Actions; no exchange orders or credentials. Report NOT RUN if local WSL2/Docker or baseline screenshot capture is unavailable. Current mock fixtures classify 30 Oct and 27 Nov as Monthly, 06 Nov as Weekly and Perp as Perpetual; Daily/Quarterly intentionally have no samples.
