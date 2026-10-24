# TO-V2-38 — TradeOps Deribit React terminal (functional vertical slice)

This workstream deliberately stops the backup treadmill and implements a
user-operated exchange terminal backed by new actual HTTP routes.

## Working source

- apps/trade-terminal — React 19 + TypeScript + Vite responsive workstation UI.
- src/TradeOps.Api/Controllers/DeribitTerminalController.cs — real Deribit
  market, private account, position and open-order HTTP reads; strictly
  manually confirmed LIMIT post-only execution and individual cancel.
- src/TradeOps.Api/Program.cs — named Deribit HTTP client with redirects,
  cookies and proxies disabled.
- src/TradeOps.Api/appsettings.json — terminal-specific safe config with
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

## Local first run — two terminals on Windows

Prerequisites:
- .NET 10 SDK, Node.js 20+ and npm
- the existing TradeOpsDb connection required by the unmodified API
  startup; the API currently runs EF migrations on its own TradeOpsDb.
  This is NOT the separate empty tradeops_evidence database.
- a local user-chosen operator key of at least 32 characters

PowerShell A (API, testnet read-only by default):

    cd D:\Projects\TradeOps-v2
    $env:DeribitTerminal__Environment = 'testnet'
    $env:DeribitTerminal__ExecutionEnabled = 'false'
    $env:DeribitTerminal__MainnetExecutionEnabled = 'false'
    $env:DeribitTerminal__MaxContracts = '100'

Prompt for a fresh local operator phrase securely:

    $secure = Read-Host 'Operator key (32+ chars, re-enter same in browser)' -AsSecureString
    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try {
      $env:DeribitTerminal__OperatorKey = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr)
    } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }

Do not paste this key into a GitHub issue, terminal command line,
appsettings.json or chat. Environment variables are readable by the
same-user local processes: do not treat this mechanism as hardened
secret management. Leave ClientId/ClientSecret unset initially to
verify PUBLIC actual quotes only.

    dotnet run --project src/TradeOps.Api --urls http://127.0.0.1:5058

PowerShell B (React):

    cd D:\Projects\TradeOps-v2\apps\trade-terminal
    npm install
    npm run build
    npm run dev

Open http://127.0.0.1:5173, enter the operator phrase. Expect testnet
market quotes and an explicitly locked order ticket. If backend startup
cannot reach the application's existing TradeOpsDb, start that DB or
configure its normal connection first; never repoint the main TradeOps
DB to tradeops_evidence. The Vite development server proxies
/api/deribit-terminal only to 127.0.0.1:5058.

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


## Preferred owner-local MAINNET read-only launch with existing DPAPI vault

The existing local Windows vault created by the qualified
Deribit-ReadOnlyLocal.ps1 (Save action) is located under CurrentUser
LOCALAPPDATA/TradeOps/Credentials and contains mainnet READ-ONLY credentials.
Use it instead of manually entering a ClientId/ClientSecret:

    scripts/deribit/Start-MainnetReactTerminalReadOnly.ps1 -ExpectedHead <PINNED_PR_HEAD>

Run through the exact pinned Git object (do not execute an unqualified
mutable local script). This Windows script:
- fetches and checks the exact TradeOps React branch commit SHA;
- creates a DETACHED temporary worktree and builds the .NET API BEFORE
  touching the credential vault;
- validates the local DPAPI vault schema/environment before decrypting;
- prompts the owner for a new 32+ character operator phrase;
- runs the mainnet account, positions and orders HTTP API on localhost;
- FORCES both ExecutionEnabled and MainnetExecutionEnabled to false, even
  if the user's global config or shell flags were true;
- prints a temporary React frontend source folder; use a second terminal
  to run npm install and npm run dev from that folder;
- clears child process environment variables, disposes SecureStrings
  and removes the temporary worktree when the API process exits.

The read-only mainnet vault must NEVER be repurposed for mainnet trading.
Real orders need a separately permissioned trade key and explicitly
approved local run configuration. This start script has no path for
enabling any trading mutation.

Note: The existing TradeOps API startup uses its own TradeOpsDb
EF migrations. If that database is offline/unconfigured, this launcher
cannot start a backend and the terminal remains unavailable; it must
not silently create or repoint the distinct dedicated evidence DB.
