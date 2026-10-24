# TO-V2-38 — TradeOps Deribit React terminal (functional vertical slice)

This workstream deliberately stops the backup treadmill and implements a
user-operated exchange terminal backed by new actual HTTP routes.

## Working source

- apps/trade-terminal — React 19 + TypeScript + Vite responsive workstation UI.
- apps/TradeOps.TerminalApi — isolated localhost ASP.NET Core 10 host, no DB, no migrations, no other controllers or workers.
- apps/TradeOps.TerminalApi/Controllers/DeribitTerminalController.cs — real Deribit
  market, private account, position and open-order HTTP reads; strictly
  manually confirmed LIMIT post-only execution and individual cancel.
- apps/TradeOps.TerminalApi/Program.cs — named Deribit HTTP client with redirects,
  cookies and proxies disabled.
- apps/TradeOps.TerminalApi/appsettings.json — terminal-specific safe config with
  ExecutionEnabled=false and MainnetExecutionEnabled=false by default.
- tests/TradeOps.UnitTests/DeribitReactTerminalSecurityTests.cs — zero-
  network checks for localhost operator authentication, fail-closed
  execution and mainnet two-switch gating.

This terminal is deliberately independent of the existing IExchangeClient
testnet adapter, DbContext persistence model and worker orchestration.
It does NOT add DI integrations or modify legacy Orders/Fills. Production
private reads use the official Deribit HTTPS JSON-RPC HTTP API and a
server-held dedicated key. Only the two supported perpetual instruments
BTC-PERPETUAL and ETH-PERPETUAL are accepted for quotes/orders here.
Positions and open orders are read for all instrument kinds.

## Current capabilities

- GET /api/deribit-terminal/status — exchange environment, readiness,
  whether manual execution is enabled, allowed instruments/max contracts.
- GET /api/deribit-terminal/ticker?instrument=BTC-PERPETUAL — actual public
  bid/ask, mark, last and index with refresh every 8 seconds.
- GET /api/deribit-terminal/account?currency=BTC — account equity, balance,
  available funds (requires server-side Deribit credentials).
- GET /api/deribit-terminal/positions — real positions with native signed
  Deribit sizes, floating PnL and mark prices.
- GET /api/deribit-terminal/orders — actual open exchange orders.
- POST /api/deribit-terminal/orders/preview — server validates a bounded
  BTC/ETH perpetual LIMIT price and native contracts, generates a unique
  preview GUID valid for 45 seconds. Requires explicit execution enablement.
- POST /api/deribit-terminal/orders/confirm — accepts single-use preview
  GUID only; server sends Deribit private/buy or private/sell with:
  type=limit, post_only=true, reject_post_only=true, contracts, label.
  The preview is consumed BEFORE the outgoing exchange request. If an
  HTTP outcome is ambiguous, no automatic retry; user must inspect Deribit.
- POST /api/deribit-terminal/orders/cancel — one order ID + exact
  "CANCEL {orderId}" text; UI also asks explicit confirmation.
  No cancel-all, automatic strategy or background trading support.

All routes require an X-TradeOps-Terminal-Key operator secret with at
least 32 characters set only in the backend local configuration,
and a loopback (127.0.0.1/::1) caller. Never expose the API port on a
public interface or deploy a reverse proxy allowing remote users. The
operator key is held in the React tab's memory, not localStorage or
sessionStorage. **This is a local single-operator prototype, not an
internet-exposed multi-user trading service**.

Credentials are supplied to Deribit via HTTPS Basic Authorization header
on the API server; they are NEVER sent to React. The exact exchange
destination is fixed to Deribit's test or production host, per the
server's Environment setting. No redirects or HTTP proxies are used.

## Local production READ-ONLY first run — ONE PowerShell, ZERO passwords

This is the recommended owner workflow. Node.js 24 + npm 11 and .NET 10
are installed locally. Do NOT use a manually entered operator passphrase.

1. Use the SHA-pinned GitHub bootstrap of
   scripts/deribit/Start-MainnetReactTerminalReadOnly.ps1 at the current
   reviewed Draft PR head. Launcher validates the exact SHA, creates
   detached worktree, builds the isolated .NET API and React with
   npm install + npm run build BEFORE reading any broker credentials.
2. The launcher decrypts the existing owner's mainnet READ-ONLY Windows
   CurrentUser DPAPI credential vault, creates a NEW 384-bit random
   operator session token automatically and starts both localhost servers.
   It never asks the user to enter, save or remember this session token.
3. The isolated .NET API inherits the read-only credentials and its
   session token; React/Vite inherits only the token. Neither receives
   the other process's private data. Browser JavaScript never sees
   the actual token: Vite's server-side proxy adds it ONLY to GET
   /api/deribit-terminal requests. POST requests are not auto-authenticated.
4. Open http://127.0.0.1:5173 — auto session runs immediately, without
   a login page or typed passphrase. Live trades stay disabled because
   the launcher FORCES ExecutionEnabled=false and
   MainnetExecutionEnabled=false, irrespective of other configs.
5. Ctrl+C in the owning PowerShell stops both child processes and
   removes the detached worktree. Do not shut down unrelated containers.

Owner result marker (not evidence until owner actually runs the new SHA):

TRADEOPS_REACT_TERMINAL_MAINNET_READONLY_STARTED auto_operator_auth=PASS proxy_status=PASS backend=127.0.0.1:5058 frontend=127.0.0.1:5173 exchange_reads=UNVERIFIED live_execution=DISABLED

The session token is **ephemeral**, not a permanent password.
It need not be remembered and is not written to disk/chat/GitHub.
A new one is generated every time the entire terminal starts. The
Deribit READ-ONLY API key remains persisted only in the existing
owner-local DPAPI vault. This is NOT login-less public internet
access; both services bind to loopback only, and Vite's auto-header
mechanism is for the local READ-ONLY mode.

## Authenticated Deribit account and testnet order acceptance

For private reads, use a **testnet** key with read/account permissions.
Supply it ONLY on the server, as ephemeral environment configuration
for this workstation. On the same PowerShell A session *before*
starting dotnet, securely input each ClientId and ClientSecret through
the local secure prompt and convert to runtime environment variables.
Do not put secrets in React or in the repository.

To try manual testnet orders, with testnet credentials capable of
trading, set ExecutionEnabled=true on the backend. When refreshing the
React page the terminal will show TESTNET / Enabled; preview one
bounded post-only LIMIT buy/sell, visually verify exact instrument,
side, native contracts and price, then click separate confirmation.
Refresh open orders; cancel one specific order if desired.

## Mainnet (real money) — separate intentional enablement

No production trade is performed, enabled or scheduled by creating this
PR. Live mainnet trading requires all of these local settings:
- DeribitTerminal__Environment = mainnet
- DeribitTerminal__ExecutionEnabled = true
- DeribitTerminal__MainnetExecutionEnabled = true
- DeribitTerminal__MaxContracts = bounded value chosen by owner
- A NEW dedicated, appropriately scoped Deribit mainnet trading API
  key configured only on the backend. NEVER enable withdrawal privileges.
- An operator key and loopback-only HTTP bind.

The existing mainnet READ-ONLY DPAPI credential used by other local
PowerShell tools is NOT used by this terminal and should NOT be
replaced/reused as a trading key. Any mainnet execution will be a real
exchange order risking real funds. Use testnet acceptance and confirm
Deribit API key trade permissions before enabling mainnet. The server
has two independent mainnet flags precisely to avoid accidental live
execution by a development front-end.

The current order ticket intentionally supports **only** non-marketable
post-only LIMIT perpetual orders, not market, stop, options or combo
orders. post_only with reject_post_only means a crossing order is
rejected instead of changed or immediately executed. An accepted
resting order may later execute when another participant trades
against it. Contract units are NATIVE Deribit contracts, not BTC,
ETH or USD conversions: review instrument contract specifications.

## What is not claimed

- This workstream is committed source but it has NOT YET been
  independently built, run or manually accepted on the owner's machine.
- No live mainnet order, testnet order or cancellation has been sent by
  the assistant.
- React UI currently uses 8-second polling, not WebSocket subscriptions.
- No new SQLite/Postgres order audit is attached to these manual routes.
  Label correlation and manual reconciliation are required after an
  uncertain exchange HTTP result. Do not automatically retry.
- Only 2 perpetuals in the order ticket; options/futures term structure,
  instruments browser, order book, trade tape, websocket streams,
  Greeks, fills, PnL and multi-account routing remain future vertical
  slices. Those are now the priority, not more backup work.
- Full .NET and npm build Gates: not yet observed.
- Keep new PR Draft until local verification; preserve earlier owner-
  qualified frozen SHAs and avoid merging unqualified stacked work.

Reference documentation:
https://docs.deribit.com/articles/deribit-quickstart
https://docs.deribit.com/articles/authentication
https://docs.deribit.com/articles/order-management-best-practices


## Mainnet production credential boundary

The existing owner-local Deribit READ-ONLY DPAPI vault is reused
automatically. Do not re-enter, paste or put credentials or terminal
operator keys in chat or Git. Mainnet manual trading will require
a separate permissioned trading key, an explicit operator decision and
a separate security/acceptance stage. Auto authorization in the
Vite server grants GET only, never POST execution.

The standalone apps/TradeOps.TerminalApi host does not initialize
TradeOpsDb, EF migrations or the tradeops_evidence database.
