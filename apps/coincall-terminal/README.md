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
- Spot and Futures: synthetic mark/price/funding, responsive chart / book / ticket, 22 book levels, disabled Buy/Sell.
- Options: representative mock chain. Data: local mock chart.
- Spreads default: BTC/ETH, margin type, Grid/Table selection, Bid/Ask/Diff and expiry filters, USD/% and MarkPrice/BidAskMedian. Click quote for buy/sell legs and disabled two-leg controls.
- Assets: account-scoped balances, orders, positions, funding, explicit nonimplemented analytics.
- Accounts: two mock account contexts; no credentials or API key form.
All market/book/order/position records are deterministic snapshot fixtures `2026-10-25T12:00:00Z`, not live trading information.

## Safety
No `fetch`, `WebSocket`, network/client SDK, localStorage, execution endpoint, imported VAN JS, order POST or authenticated credentials. Trading buttons have `disabled` HTML attributes. No claimed bot / orders-net parity.

G0/orchestrator review required; no merge. See `docs/ui/COINCALL_VAN_PARITY.md`.