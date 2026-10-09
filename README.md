# TradeOps

C#/.NET trading execution and automation backend focused on reliable order handling rather than strategy research.

TradeOps is built for the case where a client already has trading rules, signals, or an existing bot and needs the engineering layer around execution: broker/exchange integration, order lifecycle, risk controls, persistence, reconciliation, recovery, logging and alerts.

> TradeOps does **not** provide a profitable strategy, alpha, signals, or return guarantees. Mock mode is the default. Execution-capable venue integrations are restricted to non-production environments: **Bybit testnet**, **Binance USD-M Futures testnet**, **Hyperliquid testnet**, **Deribit testnet**, **OKX Demo**, **Bitget Demo**, and **Gate Futures TestNet**. **MEXC Futures, Kraken Futures, and KuCoin Futures are live-host read-only** because a usable persistent public sandbox/testnet is not currently documented for those adapters. **Coinbase International Exchange (INTX)** uses its dedicated sandbox.

## What this demo proves

- configuration-driven exchange adapters behind `IExchangeClient`;
- deterministic `ClientOrderId` generation and idempotent signal retries;
- optional shared-secret authentication for external signal ingestion, rejected before persistence on authentication failure;
- optional PostgreSQL-backed timestamp/request-id replay protection for authenticated signal ingress;
- optional HMAC-SHA256 webhook signing that binds timestamp, request ID, method, path and exact request-body bytes;
- persistent webhook-attempt audit linking request ID to signal and local order correlation identifiers;
- optional TradingView webhook adapter behind a trusted gateway, with deterministic event-id idempotency;
- production-like Nginx TradingView edge deployment with HTTPS, published source-IP allowlisting, client-certificate identity checks and internal credential injection;
- persistent TradingView delivery audit with event-level correlation, outcomes and processing-latency metrics;
- one-command Mock customer demo for the complete TradingView execution, idempotency, audit, reconciliation and health path;
- client integration starter kit for a controlled first paid TradingView pilot, including env template, preflight, alert template, runbook and acceptance checklist;
- read-only paid-pilot evidence exporter that produces JSON and Markdown handoff reports from persisted TradingView audit/correlation data;
- independent operator API-key protection for mutating control, cancellation and reconciliation actions;
- PostgreSQL persistence with a unique constraint protecting against duplicate local orders;
- no blind retry after an ambiguous exchange timeout;
- guarded order state transitions and partial-fill handling;
- local order lifecycle history, fill audit and signal outcome audit;
- order and position reconciliation;
- persistent trading controls, emergency stop and bulk cancellation;
- restart recovery and persisted operational run status through background workers;
- Bybit testnet REST integration plus authenticated private WebSocket order/execution events;
- Binance USD-M Futures testnet REST integration plus private user-data stream with mainnet host rejection;
- Hyperliquid testnet signed execution plus order/fill WebSocket integration with mainnet host rejection;
- Deribit testnet REST execution plus authenticated private WebSocket order/trade events with production-host rejection;
- OKX Demo REST execution plus authenticated private WebSocket SWAP/FUTURES order/fill events on the demo websocket host;
- MEXC Futures live-host read-only account, position and order-state integration with trading mutations disabled;
- Kraken Futures live-host read-only account, position and open-order integration with trading mutations disabled;
- KuCoin Futures live-host read-only account, position and order-state integration with trading mutations disabled;
- Coinbase INTX sandbox perpetual-futures account, position and REST order execution integration with production-host rejection;
- execution and signal-transition metrics, including window/series/by-symbol views;
- structured logging and optional fail-safe Telegram alerts;
- reproducible Docker demo;
- executable .NET signed-webhook client that demonstrates tamper rejection, replay rejection, execution, reconciliation and metrics end to end;
- CI build, unit tests and PostgreSQL-backed runtime/integration smoke tests.

## Stack

```text
C# / .NET 8
ASP.NET Core
Worker Service
EF Core 8
PostgreSQL 16
HttpClient / Bybit V5 REST / Binance USD-M Futures REST / Hyperliquid API / MEXC Contract API / Kraken Futures REST / KuCoin Futures REST
Docker / Docker Compose
GitHub Actions
```

## Solution

```text
TradeOps.sln

src/
├── TradeOps.Api
├── TradeOps.Application
├── TradeOps.Domain
├── TradeOps.Infrastructure
└── TradeOps.Worker
```

Responsibilities:

- **Domain** — entities, enums and order state rules.
- **Application** — execution use cases and exchange-independent contracts.
- **Infrastructure** — EF Core/PostgreSQL, mock exchange, test/demo exchange adapters, live-host read-only adapters for venues without a usable public sandbox, and Telegram adapter.
- **Api** — signal input, exchange/account monitoring, order lookup/cancellation and manual reconciliation endpoints.
- **Worker** — restart recovery, reconnect and periodic reconciliation.

## Exchange architecture

All venues implement the same application contract:

```text
Application
    |
    v
IExchangeClient
    |
    +--> execution-capable test/demo/sandbox adapters
    +--> live-host read-only adapters
```

TradeOps also exposes an `IExchangeCapabilityCatalog` so execution safety is data-driven instead of inferred from provider names.

| Provider | Environment | Execution | Private events | Host mode |
| --- | --- | ---: | ---: | --- |
| Mock | Mock | yes | no | local |
| BybitTestnet | Testnet | yes | yes | non-production |
| BinanceFuturesTestnet | Testnet | yes | yes | non-production |
| HyperliquidTestnet | Testnet | yes | yes | non-production |
| MexcFuturesReadOnly | LiveReadOnly | no | no | live |
| DeribitTestnet | Testnet | yes | yes | non-production |
| OkxDemo | Demo | yes | yes | live REST + simulated-trading header / demo WebSocket |
| BitgetDemo | Demo | yes | no | live REST + demo header |
| GateFuturesTestnet | Testnet | yes | no | non-production |
| KrakenFuturesReadOnly | LiveReadOnly | no | no | live |
| KuCoinFuturesReadOnly | LiveReadOnly | no | no | live |
| CoinbaseIntxSandbox | Sandbox | yes | no | non-production |

The selected adapter is controlled by configuration:

```json
{
  "Exchange": {
    "Provider": "Mock"
  }
}
```

Supported providers in the current public version:

```text
Mock
BybitTestnet
BinanceFuturesTestnet
HyperliquidTestnet
MexcFuturesReadOnly
DeribitTestnet
OkxDemo
BitgetDemo
GateFuturesTestnet
KrakenFuturesReadOnly
KuCoinFuturesReadOnly
CoinbaseIntxSandbox
```

## Broker-neutral research decision boundary

TradeOps now has a validation-only contract for research or AI systems that produce structured trading intent before any order is created.

```text
Research / AI
    |
    v
POST /api/research-decisions/validate
    |
    v
broker-neutral InstrumentReference + ResearchDecision
    |
    v
future portfolio / risk translation
    |
    v
existing TradeOps execution lifecycle
```

The contract supports stocks and other asset classes without assuming crypto-style symbols. Instrument identity can carry a venue-specific identifier such as an Interactive Brokers contract ID, plus currency and routing exchange.

Research decisions are deliberately not executable orders. They can express `Buy`, `Sell`, `Exit`, `NoAction`, or `SetTargetWeight`, but a later deterministic portfolio/risk layer must translate them into orders.

Example:

```json
{
  "decisionId": "AAPL-Q4-2026-guidance",
  "strategyId": "earnings-quality-v1",
  "instrument": {
    "symbol": "AAPL",
    "assetClass": "Stock",
    "currency": "USD",
    "venueInstrumentId": "265598",
    "exchange": "SMART"
  },
  "action": "SetTargetWeight",
  "generatedAt": "2026-10-06T12:00:00Z",
  "targetWeight": 0.04,
  "confidence": 0.82,
  "sourceEventId": "AAPL-Q4-2026",
  "reason": "guidance-raised"
}
```

## Execution flow

```text
External signal
      |
      v
POST /api/signals
      |
      v
Optional shared-secret authentication
      |
      v
Optional timestamp + request-id replay protection
      |
      v
Optional HMAC-SHA256 request-body verification
      |
      v
Deterministic ClientOrderId
      |
      +---- existing order ----> return existing state
      |
      v
RiskEngine
      |
 allow / reject
      |
      v
Persist Created
      |
      v
Created -> Submitted
      |
      v
IExchangeClient.PlaceOrderAsync
      |
      v
exchange state / acknowledgement
      |
      v
Persist exchange state
      |
      +---- structured log
      +---- optional Telegram alert
```

A timeout does **not** trigger a blind second `PlaceOrderAsync`. TradeOps first looks up the exchange order by `ClientOrderId`; if the exchange state cannot be confirmed, the local order becomes `Unknown` and must be reconciled.

## Recovery flow

```text
TradeOps.Worker starts
      |
      v
EnsureConnectedAsync
      |
      +---- failure ---> alert ---> exponential backoff ---> retry
      |
      v
Load non-terminal persisted orders
      |
      v
Query exchange state
      |
      +---- valid ------> OrderStateMachine -> persist
      +---- missing ----> Unknown + issue
      +---- mismatch ---> issue; do not overwrite blindly
      |
      v
Periodic reconciliation
```

In mock mode, synthetic exchange IDs allow the separate Worker process to reconstruct mock state after restart. In Bybit testnet mode, the Worker queries Bybit through the same `IExchangeClient` contract.

## Quick start with Docker — mock mode

Requirements:

- Docker Engine / Docker Desktop;
- Docker Compose v2;
- `curl` only if you want to run the included scripts from the host.

Start the complete stack:

```bash
docker compose up --build -d
```

Mock is the default provider. This starts:

```text
PostgreSQL 16  -> localhost:54321
TradeOps.Api   -> http://localhost:8080
TradeOps.Worker
```

The API automatically applies the committed EF Core migration on startup.

Check health:

```bash
curl http://localhost:8080/health
```

Run the deterministic mock demo:

```bash
bash scripts/demo.sh
```

For the secured commercial-style flow, enable signal authentication, replay protection, HMAC signing and operator authentication, then run the .NET end-to-end client:

```bash
export TRADEOPS_SIGNAL_AUTH_ENABLED=true
export TRADEOPS_SIGNAL_API_KEY='local-demo-signal-api-key'
export TRADEOPS_SIGNAL_REPLAY_ENABLED=true
export TRADEOPS_SIGNAL_SIGNING_ENABLED=true
export TRADEOPS_SIGNAL_SIGNING_SECRET='local-demo-signing-secret-at-least-32-characters'
export TRADEOPS_OPERATOR_AUTH_ENABLED=true
export TRADEOPS_OPERATOR_API_KEY='local-demo-operator-api-key'

docker compose up --build -d

export TRADEOPS_DEMO_API_URL='http://localhost:8080'
export TRADEOPS_DEMO_SIGNAL_API_KEY='local-demo-signal-api-key'
export TRADEOPS_DEMO_SIGNING_SECRET='local-demo-signing-secret-at-least-32-characters'
export TRADEOPS_DEMO_OPERATOR_API_KEY='local-demo-operator-api-key'

dotnet run --project tools/TradeOps.SignedWebhookDemo
```

The final line should be `SIGNED WEBHOOK E2E DEMO: PASS`. See `docs/signed-webhook-e2e-demo.md` for the complete sequence.

Stop the stack:

```bash
docker compose down
```

Delete the demo database as well:

```bash
docker compose down -v
```

## Mock demo scenario

The script uses the fixed logical signal ID:

```text
d0f8625f-2ad4-44eb-a1ec-22acbfbb2e58
```

### 1. First submission

```json
{
  "symbol": "BTCUSDT",
  "side": "Buy",
  "quantity": 0.001,
  "source": "docker-demo",
  "signalId": "d0f8625f-2ad4-44eb-a1ec-22acbfbb2e58"
}
```

Expected mock state:

```text
RequestedQuantity = 0.001
FilledQuantity    = 0.0006
Status            = PartiallyFilled
```

### 2. Reconciliation

`POST /api/system/reconcile` queries the mock exchange again.

Expected state:

```text
FilledQuantity = 0.001
Status         = Filled
```

### 3. Idempotent retry

The exact same `signalId` is submitted again. TradeOps derives the same `ClientOrderId`, loads the existing persisted order and returns it instead of creating a duplicate.

## Bybit testnet

The Bybit adapter uses the V5 REST API and is deliberately restricted in code to:

```text
https://api-testnet.bybit.com
```

Mainnet URLs are rejected by `BybitExchangeClient` in this version.

Credentials are supplied only through environment/configuration. Never commit them.

For Docker Compose:

```bash
export TRADEOPS_EXCHANGE_PROVIDER=BybitTestnet
export BYBIT_TESTNET_API_KEY='YOUR_TESTNET_KEY'
export BYBIT_TESTNET_API_SECRET='YOUR_TESTNET_SECRET'
```

Then start the stack normally:

```bash
docker compose up --build -d
```

For a **read-only** authentication/integration check that does not place an order:

```bash
bash scripts/bybit-testnet-readonly-smoke.sh
```

The script queries:

```text
GET /api/account
GET /api/positions
GET /api/orders
```

The default Bybit configuration is:

```json
{
  "Exchange": {
    "Provider": "Mock",
    "Bybit": {
      "BaseUrl": "https://api-testnet.bybit.com",
      "ApiKey": "",
      "ApiSecret": "",
      "Category": "linear",
      "SettleCoin": "USDT",
      "AccountType": "UNIFIED",
      "RecvWindowMilliseconds": 5000,
      "HttpTimeoutSeconds": 10
    }
  }
}
```

The adapter currently implements the exchange contract for:

```text
GetAccountAsync
GetPositionsAsync
GetOpenOrdersAsync
GetOrderAsync
GetOrderByClientOrderIdAsync
PlaceOrderAsync
CancelOrderAsync
```

Connection readiness/recovery is handled separately through the exchange connection manager and background Worker. Bybit private WebSocket order/execution events are consumed through `IExchangeEventStream`.

Bybit order acknowledgements are asynchronous. A successful create/cancel HTTP acknowledgement is therefore not treated as proof of a fill or final cancellation; subsequent lookup/reconciliation confirms state.

## Binance USD-M Futures testnet

TradeOps v1.4.1.0 adds a REST adapter for Binance USD-M Futures testnet.

The adapter is restricted in code to approved non-production Binance Futures hosts. Production `fapi.binance.com` is rejected.

For Docker Compose:

```bash
export TRADEOPS_EXCHANGE_PROVIDER=BinanceFuturesTestnet
export BINANCE_FUTURES_TESTNET_API_KEY='YOUR_TESTNET_KEY'
export BINANCE_FUTURES_TESTNET_API_SECRET='YOUR_TESTNET_SECRET'
docker compose up --build -d
```

Run the read-only authentication/integration smoke test:

```bash
bash scripts/binance-futures-testnet-readonly-smoke.sh
```

The smoke test calls account, positions and open-orders endpoints only; it does not place an order.

The REST adapter implements:

```text
GetAccountAsync
GetPositionsAsync
GetOpenOrdersAsync
GetOrderAsync
GetOrderByClientOrderIdAsync
PlaceOrderAsync
CancelOrderAsync
```

Binance order lookup and cancellation require symbol context. TradeOps passes the persisted local order symbol through reconciliation and cancellation paths. For direct exchange API routes, provide `?symbol=BTCUSDT` when querying or cancelling a Binance order.

TradeOps v1.4.2.x also consumes the Binance USD-M Futures user-data stream through `IExchangeEventStream`. The Worker:

```text
creates/renews listenKey
-> connects to approved testnet/demo websocket host
-> receives ORDER_TRADE_UPDATE
-> maps order + execution updates
-> persists lifecycle/fills through ExchangeEventProcessor
-> reconnects through the existing Worker backoff loop on expiry/disconnect
```

Default stream settings:

```json
{
  "Exchange": {
    "Binance": {
      "PrivateWebSocketBaseUrl": "wss://stream.binancefuture.com",
      "ListenKeyKeepaliveMinutes": 30
    }
  }
}
```

Production Binance websocket hosts are rejected. The stream credentials are never embedded in the websocket URL; the temporary listen key is created through the Binance USER_STREAM lifecycle endpoint and closed on shutdown when possible.

## Hyperliquid testnet

TradeOps v1.5.x supports Hyperliquid testnet account/order reads and signed perpetual execution.

Read-side integration uses the public `/info` endpoint:

```text
clearinghouseState
frontendOpenOrders
orderStatus
meta
allMids
```

Signed execution uses the testnet `/exchange` endpoint with the Hyperliquid L1 signing scheme:

```text
ordered MessagePack action
-> nonce + null vault marker
-> Keccak-256 action hash
-> testnet phantom Agent
-> EIP-712 signature
-> /exchange
```

The C# signer is covered by official Hyperliquid Python SDK signature vectors, including an order with a deterministic client order id.

Configuration:

```bash
export TRADEOPS_EXCHANGE_PROVIDER=HyperliquidTestnet
export HYPERLIQUID_TESTNET_USER_ADDRESS='0xYOUR_MASTER_OR_SUBACCOUNT_ADDRESS'
export HYPERLIQUID_TESTNET_PRIVATE_KEY='0xYOUR_DEDICATED_TESTNET_API_WALLET_PRIVATE_KEY'
export HYPERLIQUID_MARKET_SLIPPAGE_PERCENT=5
docker compose up --build -d
```

Use a dedicated Hyperliquid API/agent wallet for automated signing. `UserAddress` remains the actual account address whose state is queried; the signing key may belong to an approved API wallet.

Never commit the private key.

Read-only smoke remains available:

```bash
bash scripts/hyperliquid-testnet-readonly-smoke.sh
```

Supported exchange contract:

```text
GetAccountAsync
GetPositionsAsync
GetOpenOrdersAsync
GetOrderAsync
GetOrderByClientOrderIdAsync
PlaceOrderAsync
CancelOrderAsync
```

For standard perpetuals, TradeOps resolves `asset` from the index in `meta.universe` and enforces `szDecimals`. Builder-deployed HIP-3 perps are intentionally excluded from this milestone because their asset-id encoding is different.

Hyperliquid has no native market-order primitive in this API path. TradeOps maps a TradeOps `Market` order to an aggressive IOC limit around the current mid price using the configured slippage percentage. A TradeOps `Limit` order maps to GTC.

TradeOps converts its arbitrary `ClientOrderId` into a deterministic 128-bit Hyperliquid `cloid`. This allows ambiguous placement recovery through `orderStatus` without blindly submitting the order again.

TradeOps v1.5.2.x also consumes the official Hyperliquid testnet websocket:

```text
wss://api.hyperliquid-testnet.xyz/ws
```

The Worker subscribes to:

```text
orderUpdates
userFills
```

and forwards normalized order/fill events through the existing `IExchangeEventStream -> ExchangeEventProcessor` pipeline.

Hyperliquid `userFills` does not include the client order id. TradeOps therefore resolves websocket events by local `ClientOrderId` when available and falls back to persisted `ExchangeOrderId` / Hyperliquid `oid`. Fill IDs use the globally unique combination of fill time, coin and Hyperliquid trade id.

The stream is configured with:

```json
{
  "Exchange": {
    "Hyperliquid": {
      "WebSocketUrl": "wss://api.hyperliquid-testnet.xyz/ws"
    }
  }
}
```

The Worker already reconnects event streams with exponential backoff when Hyperliquid disconnects. The mainnet websocket endpoint is rejected.

The adapter accepts only the official `https://api.hyperliquid-testnet.xyz` REST host and `wss://api.hyperliquid-testnet.xyz/ws` websocket endpoint. Mainnet endpoints are rejected.

## MEXC Futures read-only

TradeOps v1.6.0.0 adds a deliberately read-only MEXC Futures adapter.

MEXC's official Contract API documentation currently publishes the live API host:

```text
https://contract.mexc.com
```

and does not publish a separate Contract API sandbox/testnet. The documented place-order and cancel-order endpoints are also marked as under maintenance. For those reasons TradeOps does **not** expose MEXC order placement or cancellation in this milestone.

Provider:

```text
MexcFuturesReadOnly
```

The adapter supports:

```text
GetAccountAsync
GetPositionsAsync
GetOpenOrdersAsync
GetOrderAsync
GetOrderByClientOrderIdAsync (symbol required)
```

It intentionally rejects:

```text
PlaceOrderAsync
CancelOrderAsync
```

Use a MEXC API key with only the minimum read permissions required by the endpoints: account read and trade-information read. Do not grant transaction-modify permission for this adapter.

Configuration:

```bash
export TRADEOPS_EXCHANGE_PROVIDER=MexcFuturesReadOnly
export MEXC_FUTURES_API_KEY='YOUR_READ_ONLY_KEY'
export MEXC_FUTURES_API_SECRET='YOUR_READ_ONLY_SECRET'
docker compose up --build -d
```

Read-only smoke:

```bash
bash scripts/mexc-futures-readonly-smoke.sh
```

The smoke script reads account state, positions and open orders only.

MEXC contract quantities are expressed in contract volume. TradeOps converts them to base-asset quantity using the public `contractSize` metadata. Position mark price is taken from MEXC `fairPrice`.

Private GET requests use the documented MEXC Contract API signing scheme:

```text
ApiKey + Request-Time + sorted/url-encoded request parameters
-> HMAC-SHA256(secret)
-> lowercase hexadecimal Signature header
```

The adapter is restricted to the official `https://contract.mexc.com` host. This restriction does **not** make the host a test environment; it remains a live exchange endpoint, which is why mutation methods stay disabled.

## Deribit testnet

TradeOps v1.7.0.0 adds Deribit futures/perpetual REST execution against the dedicated Deribit test environment:

```text
https://test.deribit.com/api/v2
```

Provider:

```text
DeribitTestnet
```

The adapter supports account summary, futures positions, open orders, order lookup by exchange order id or Deribit label, buy/sell order placement and cancellation. Authentication uses `public/auth` with `client_credentials` and caches the OAuth access token before expiry.

Configuration:

```bash
export TRADEOPS_EXCHANGE_PROVIDER=DeribitTestnet
export DERIBIT_TESTNET_CLIENT_ID='YOUR_TESTNET_CLIENT_ID'
export DERIBIT_TESTNET_CLIENT_SECRET='YOUR_TESTNET_CLIENT_SECRET'
export DERIBIT_ACCOUNT_CURRENCY='BTC'
docker compose up --build -d
```

Smoke:

```bash
bash scripts/deribit-testnet-smoke.sh
```

For this adapter, `Symbol` must be a native Deribit instrument name such as `BTC-PERPETUAL` or a dated future. `Quantity` is expressed in Deribit contract units and is sent through the API `contracts` parameter. TradeOps does not silently reinterpret `BTCUSDT` as a Deribit instrument.

The adapter accepts only the official testnet host. `https://www.deribit.com/api/v2` is rejected.

## OKX Demo

TradeOps v1.8.0.0 adds OKX derivatives REST integration for the official Demo Trading environment.

Provider:

```text
OkxDemo
```

OKX Demo uses the same REST hostname as production, so the safety boundary is enforced in the request code: every authenticated request from this provider always includes:

```text
x-simulated-trading: 1
```

There is no configuration switch that disables this header.

Configuration:

```bash
export TRADEOPS_EXCHANGE_PROVIDER=OkxDemo
export OKX_DEMO_API_KEY='YOUR_DEMO_KEY'
export OKX_DEMO_API_SECRET='YOUR_DEMO_SECRET'
export OKX_DEMO_PASSPHRASE='YOUR_DEMO_PASSPHRASE'
export OKX_ACCOUNT_CURRENCY='USDT'
export OKX_TRADE_MODE='cross'
docker compose up --build -d
```

The adapter supports balance, positions, pending/recent order lookup, deterministic client-order correlation, market/limit placement and cancellation. Request signing uses the documented OKX HMAC-SHA256/Base64 pre-hash scheme.

`Symbol` must be a native OKX derivatives instrument ID such as `BTC-USDT-SWAP` or a dated futures instrument. `Quantity` maps directly to OKX `sz`. The initial adapter assumes the account is configured for net-position operation; long/short position-mode specific order fields are intentionally not inferred.

Smoke:

```bash
bash scripts/okx-demo-smoke.sh
```

## Bitget Demo

TradeOps v1.9.0.0 adds Bitget UTA v3 futures REST integration for Demo Trading.

Provider:

```text
BitgetDemo
```

Bitget Demo uses `https://api.bitget.com`, the same REST host used by normal API traffic. TradeOps therefore enforces Demo Trading in code: every authenticated request from this provider includes:

```text
paptrading: 1
```

There is no configuration option that removes that header. Use a Bitget **Demo API Key**, not a production trading key.

Configuration:

```bash
export TRADEOPS_EXCHANGE_PROVIDER=BitgetDemo
export BITGET_DEMO_API_KEY='YOUR_DEMO_KEY'
export BITGET_DEMO_API_SECRET='YOUR_DEMO_SECRET'
export BITGET_DEMO_PASSPHRASE='YOUR_DEMO_PASSPHRASE'
export BITGET_CATEGORY='USDT-FUTURES'
export BITGET_ACCOUNT_CURRENCY='USDT'
export BITGET_MARGIN_MODE='crossed'
docker compose up --build -d
```

The REST adapter uses UTA v3 and supports account assets, current futures positions, open orders, order lookup, market/limit placement and cancellation. `clientOid` is deterministic and limited to 32 alphanumeric characters so ambiguous placement can be reconciled without blind resubmission.

Supported categories are deliberately restricted to:

```text
USDT-FUTURES
USDC-FUTURES
COIN-FUTURES
```

For USDT/USDC futures, Bitget defines `qty` in base coin, matching TradeOps quantity semantics. This initial adapter assumes one-way position mode; it does not infer hedge-mode `posSide` intent from a generic buy/sell signal.

Smoke:

```bash
bash scripts/bitget-demo-smoke.sh
```

## Gate Futures TestNet

TradeOps v2.0.0.0 adds Gate perpetual futures REST execution against the official Gate Futures TestNet:

```text
https://api-testnet.gateapi.io/api/v4
```

Provider:

```text
GateFuturesTestnet
```

Gate TestNet is a separate environment with separate API keys. Production Gate API keys are not used by this adapter.

Configuration:

```bash
export TRADEOPS_EXCHANGE_PROVIDER=GateFuturesTestnet
export GATE_TESTNET_API_KEY='YOUR_TESTNET_KEY'
export GATE_TESTNET_API_SECRET='YOUR_TESTNET_SECRET'
export GATE_TESTNET_SETTLE='usdt'
docker compose up --build -d
```

The adapter supports account state, current positions, open orders, lookup by exchange order ID or deterministic custom `text`, market/limit placement, cancellation, and ambiguous-placement recovery.

Private requests use Gate API v4 signing:

```text
METHOD
/api/v4 + request path
query string
SHA512(body)
Unix timestamp seconds
-> HMAC-SHA512(secret)
-> lowercase hexadecimal SIGN
```

`Symbol` must be a native Gate contract such as `BTC_USDT`. `Quantity` is a positive whole number of contracts. Buy/sell direction is represented by positive/negative Gate `size`.

TradeOps maps `Market` to `price=0, tif=ioc` and `Limit` to `tif=gtc`. Client order identity is encoded in Gate's `text` field with the required `t-` prefix.

The adapter accepts only the official Gate Futures TestNet API endpoint. Production Gate hosts are rejected.

Smoke:

```bash
bash scripts/gate-futures-testnet-smoke.sh
```

## Kraken Futures read-only

TradeOps v2.1.0.0 adds Kraken Futures as a live-host read-only adapter.

Kraken's support material was updated on July 7, 2026 to state that the existing public demo environment at `demo-futures.kraken.com` would be decommissioned on July 14, 2026. Because no replacement persistent public demo endpoint is currently documented, TradeOps does not enable Kraken order placement or cancellation.

Provider:

```text
KrakenFuturesReadOnly
```

The adapter uses:

```text
https://futures.kraken.com/derivatives/api/v3
```

and supports account state, open positions, open orders, and open-order lookup by exchange/client order ID. Private requests use Kraken Futures `APIKey` / `Authent` signing with SHA-256 followed by HMAC-SHA512 over the Base64-decoded API secret.

```bash
export TRADEOPS_EXCHANGE_PROVIDER=KrakenFuturesReadOnly
export KRAKEN_FUTURES_API_KEY='YOUR_READ_ONLY_KEY'
export KRAKEN_FUTURES_API_SECRET='YOUR_BASE64_SECRET'
export KRAKEN_FUTURES_ACCOUNT_CURRENCY='USD'
bash scripts/kraken-futures-readonly-smoke.sh
```

`PlaceOrderAsync` and `CancelOrderAsync` throw `NotSupportedException` locally.

## KuCoin Futures read-only

TradeOps v2.1.0.0 also adds KuCoin Futures as a live-host read-only adapter.

KuCoin suspended its Sandbox web/API services in July 2023. The current Futures API exposes an `/api/v1/orders/test` endpoint, but test orders do not enter the matching engine and cannot subsequently be queried. That is insufficient for TradeOps lifecycle/reconciliation guarantees, so trading mutations remain disabled.

Provider:

```text
KuCoinFuturesReadOnly
```

The adapter uses:

```text
https://api-futures.kucoin.com
```

and supports futures account overview, open positions, open orders, lookup by order ID, and lookup by `clientOid`.

Authentication uses the documented KuCoin headers:

```text
KC-API-KEY
KC-API-SIGN
KC-API-TIMESTAMP
KC-API-PASSPHRASE
KC-API-KEY-VERSION
```

```bash
export TRADEOPS_EXCHANGE_PROVIDER=KuCoinFuturesReadOnly
export KUCOIN_FUTURES_API_KEY='YOUR_READ_ONLY_KEY'
export KUCOIN_FUTURES_API_SECRET='YOUR_SECRET'
export KUCOIN_FUTURES_PASSPHRASE='YOUR_PASSPHRASE'
export KUCOIN_FUTURES_KEY_VERSION='2'
export KUCOIN_FUTURES_ACCOUNT_CURRENCY='USDT'
bash scripts/kucoin-futures-readonly-smoke.sh
```

`PlaceOrderAsync` and `CancelOrderAsync` are blocked locally. TradeOps will only enable KuCoin execution if a persistent test environment with queryable order lifecycle becomes available.

## Coinbase INTX sandbox

TradeOps v2.2.0.0 adds Coinbase International Exchange perpetual-futures REST execution against the dedicated INTX sandbox:

```text
https://api-n5e1.coinbase.com/api/v1
```

Provider:

```text
CoinbaseIntxSandbox
```

The adapter supports portfolio account state, perpetual positions, open orders, order lookup, market/limit placement, cancellation, and ambiguous-placement recovery. It uses native INTX instrument symbols such as `BTC-PERP`.

Authentication follows the official INTX REST scheme:

```text
timestamp + HTTP method + /api/v1 request path + exact JSON body
-> HMAC-SHA256(Base64-decoded signing key)
-> Base64 CB-ACCESS-SIGN
```

Required headers are `CB-ACCESS-KEY`, `CB-ACCESS-PASSPHRASE`, `CB-ACCESS-SIGN`, and `CB-ACCESS-TIMESTAMP`.

TradeOps maps arbitrary local `ClientOrderId` values to deterministic 32-character exchange IDs. Ambiguous placement first checks open orders by `client_order_id`; if the order filled immediately, it falls back to portfolio fills filtered by the same client ID instead of blindly re-submitting.

Configuration:

```bash
export TRADEOPS_EXCHANGE_PROVIDER=CoinbaseIntxSandbox
export COINBASE_INTX_SANDBOX_ACCESS_KEY='YOUR_SANDBOX_ACCESS_KEY'
export COINBASE_INTX_SANDBOX_PASSPHRASE='YOUR_SANDBOX_PASSPHRASE'
export COINBASE_INTX_SANDBOX_SIGNING_KEY='YOUR_BASE64_SANDBOX_SIGNING_KEY'
export COINBASE_INTX_SANDBOX_PORTFOLIO_ID='YOUR_SANDBOX_PORTFOLIO_ID'
export COINBASE_INTX_ACCOUNT_CURRENCY='USDC'
docker compose up --build -d
```

Smoke:

```bash
bash scripts/coinbase-intx-sandbox-smoke.sh
```

The adapter accepts only `https://api-n5e1.coinbase.com/api/v1`. Production `https://api.international.coinbase.com/api/v1` is rejected. REST is the first milestone; INTX FIX/drop-copy streaming remains separate.

## API

Swagger/OpenAPI is exposed by the running API. Main routes:

```text
GET    /health
GET    /health/live
GET    /health/ready

GET    /api/account
GET    /api/positions
GET    /api/positions/local

POST   /api/signals
GET    /api/signals
GET    /api/signals/{id}
GET    /api/signals/{id}/history
GET    /api/signal-ingress/requests/{requestId}
POST   /api/integrations/tradingview
GET    /api/integrations/tradingview/operations/deliveries
GET    /api/integrations/tradingview/operations/metrics
GET    /api/integrations/tradingview/operations/health

GET    /api/orders
GET    /api/orders/{exchangeOrderId}
GET    /api/orders/by-client/{clientOrderId}
DELETE /api/orders/{exchangeOrderId}

GET    /api/orders/local
GET    /api/orders/local/{idOrClientOrderId}
GET    /api/orders/local/{idOrClientOrderId}/history
POST   /api/orders/local/{idOrClientOrderId}/cancel
POST   /api/orders/local/cancel-all

GET    /api/fills
GET    /api/pnl/daily

GET    /api/risk
POST   /api/risk/trading-enabled
POST   /api/risk/emergency-stop
GET    /api/risk/events

POST   /api/system/reconcile
POST   /api/system/reconcile/positions
GET    /api/system/reconciliation/status
GET    /api/system/recovery/status
GET    /api/system/runs

GET    /api/exchange/capabilities
GET    /api/exchange/capabilities/current

GET    /api/metrics/execution
GET    /api/metrics/execution/window
GET    /api/metrics/execution/series
GET    /api/metrics/execution/by-symbol

GET    /api/metrics/signal-transitions/window
GET    /api/metrics/signal-transitions/series
GET    /api/metrics/signal-transitions/by-symbol
```

## Signal ingress authentication

`POST /api/signals` can be protected with a shared API key. Authentication is disabled by default for the local mock demo, but can be enabled without code changes.

Configuration:

```json
{
  "SignalIngress": {
    "Authentication": {
      "Enabled": true,
      "HeaderName": "X-TradeOps-Api-Key",
      "ApiKey": "YOUR_SECRET"
    }
  }
}
```

For Docker Compose:

```bash
export TRADEOPS_SIGNAL_AUTH_ENABLED=true
export TRADEOPS_SIGNAL_API_KEY='YOUR_SECRET'
docker compose up --build -d
```

Authenticated signal submission:

```bash
curl -X POST http://localhost:8080/api/signals \
  -H 'Content-Type: application/json' \
  -H 'X-TradeOps-Api-Key: YOUR_SECRET' \
  -d '{"symbol":"BTCUSDT","side":"Buy","quantity":0.001}'
```

When authentication is enabled, a missing or incorrect key returns HTTP `401` before request validation, persistence, risk evaluation or exchange execution. The middleware hashes both values and uses a fixed-time comparison; the configured secret is never written to logs or responses. Startup validation fails if authentication is enabled without a header name or API key.

### Replay protection

Authenticated signal ingress can additionally require a fresh timestamp and unique request ID:

```text
X-TradeOps-Timestamp: <Unix seconds>
X-TradeOps-Request-Id: <GUID>
```

Enable it in Docker Compose with:

```bash
export TRADEOPS_SIGNAL_AUTH_ENABLED=true
export TRADEOPS_SIGNAL_API_KEY='YOUR_SECRET'
export TRADEOPS_SIGNAL_REPLAY_ENABLED=true
docker compose up --build -d
```

The default acceptance window is ±300 seconds. Accepted request IDs are persisted in PostgreSQL before signal validation/execution, so duplicate delivery is rejected across API restarts and multiple API instances sharing the database. Receipts are retained for 600 seconds by default and expired rows are pruned opportunistically.

Replay protection prevents stale requests and reuse of the same request ID. HMAC signing can additionally bind the replay metadata to the exact request body.

### HMAC-signed webhook contract

Enable signing only together with signal API-key authentication and replay protection:

```bash
export TRADEOPS_SIGNAL_AUTH_ENABLED=true
export TRADEOPS_SIGNAL_API_KEY='YOUR_API_KEY'
export TRADEOPS_SIGNAL_REPLAY_ENABLED=true
export TRADEOPS_SIGNAL_SIGNING_ENABLED=true
export TRADEOPS_SIGNAL_SIGNING_SECRET='A_SEPARATE_SIGNING_SECRET_AT_LEAST_32_CHARACTERS'
docker compose up --build -d
```

A signed request includes:

```text
X-TradeOps-Api-Key: <API key>
X-TradeOps-Timestamp: <Unix seconds>
X-TradeOps-Request-Id: <GUID>
X-TradeOps-Signature: sha256=<HMAC-SHA256 hex digest>
```

The canonical HMAC input is:

```text
<timestamp>\n
<request-id D format>\n
<UPPERCASE method>\n
<request path>\n
<exact raw body bytes>
```

The signature is verified before the request ID is registered in PostgreSQL. A bad or tampered signature therefore cannot consume a nonce. The signing secret is separate from the API key. The default signed-body limit is 64 KiB.

See `docs/signal-ingress-hmac-signing.md` for the complete contract.

### Webhook request audit and correlation

For replay-protected requests, TradeOps records each authenticated HTTP attempt separately and correlates successful execution to the persisted signal and order:

```text
requestId
  -> ingress attempt outcome
  -> signalId
  -> orderId / ClientOrderId
  -> lifecycle / fills / reconciliation
```

Lookup all attempts for one request ID:

```text
GET /api/signal-ingress/requests/{requestId}
```

This preserves the original accepted attempt and later replay attempts as separate rows. The audit stores operational metadata only; request bodies, API keys, HMAC signatures and signing secrets are not persisted.

See `docs/signal-ingress-request-audit.md`.

## TradingView webhook adapter

TradeOps v1.2.0.0 adds an optional provider-specific adapter:

```text
POST /api/integrations/tradingview
```

It is disabled by default. The adapter is deliberately separate from the signed `/api/signals` ingress so the core HMAC/replay contract is not weakened for provider compatibility.

Recommended boundary:

```text
TradingView
  -> trusted HTTPS gateway / reverse proxy
  -> internal TradingView gateway credential
  -> TradeOps adapter
  -> canonical TradingSignal
  -> existing risk / execution / persistence / reconciliation
```

The adapter requires a stable `eventId`. TradeOps derives a deterministic signal ID from it, making provider redelivery idempotent. Reusing the same event ID with conflicting execution fields returns HTTP 409.

TradeOps v1.2.0.1 also includes a deployable Nginx edge for this boundary:

```text
deploy/tradingview-gateway/
├── nginx.conf.template
├── docker-compose.tradingview-gateway.yml
└── README.md
```

The public endpoint is `https://<host>/webhooks/tradingview`. It accepts only TradingView's published webhook source IPs, requires a presented client certificate whose subject identifies `webhook-server@tradingview.com`, overwrites the internal gateway credential, and proxies only to the internal adapter.

TradeOps v1.2.1.0 adds provider-delivery observability. Each authenticated delivery is stored separately with outcome, HTTP result, processing latency and signal/order correlation. The webhook response exposes `X-TradeOps-TradingView-Delivery-Id`.

Read recent or event-specific deliveries:

```text
GET /api/integrations/tradingview/operations/deliveries?eventId=<eventId>&limit=50
```

Read default 24-hour delivery metrics:

```text
GET /api/integrations/tradingview/operations/metrics
```

TradeOps v1.2.1.1 adds stateful health evaluation and transition-based operational alerts. The Worker persists the latest health state, suppresses repeated alerts while the state is unchanged, and emits a recovery notification only when health returns to `Healthy`.

```text
GET /api/integrations/tradingview/operations/health
```

Health monitoring is disabled by default. Enable it with `TRADEOPS_TRADINGVIEW_HEALTH_ENABLED=true`. Rate-based checks require a minimum sample size; no-success monitoring is separately opt-in.

For a customer-facing local demonstration, run:

```bash
bash scripts/tradingview-customer-demo.sh
```

The demo uses Mock execution only and leaves the stack running for inspection. It proves provider redelivery idempotency, event-id conflict protection, delivery correlation, protected reconciliation, lifecycle completion, metrics and Worker health. See `docs/customer-tradingview-demo.md`.

For a first paid pilot, start with the client-facing offer in `docs/paid-pilot-offer.md`, then use the integration kit:

```text
deploy/client-starter/
docs/client-pilot-runbook.md
docs/client-pilot-acceptance-criteria.md
scripts/client-pilot-preflight.sh
```

The starter kit keeps Mock as the first acceptance stage, supports an optional BybitTestnet phase only after Mock sign-off, and explicitly excludes strategy profitability/mainnet acceptance.

After the required test event has completed, v1.3.2.1 can generate an operator-authenticated client handoff evidence package:

```bash
export TRADEOPS_PILOT_EVENT_ID='THE_APPROVED_EVENT_ID'
export TRADEOPS_OPERATOR_API_KEY='THE_CONFIGURED_OPERATOR_KEY'
bash scripts/generate-pilot-evidence.sh
```

The exporter reads existing monitoring/audit endpoints and writes `pilot-evidence.json` plus `pilot-evidence.md`. Sensitive reads used by the evidence path require the operator credential when operator authentication is enabled. The exporter does not place, cancel or reconcile orders. See `docs/paid-pilot-evidence-handoff.md`.

See `docs/tradingview-webhook-adapter.md`, `docs/tradingview-delivery-audit.md`, `docs/tradingview-delivery-health-alerting.md` and `deploy/tradingview-gateway/README.md`.

## Operator API authentication

Operator actions and the sensitive paid-pilot evidence read path can be protected with a credential independent from the external signal-ingress key. Authentication is disabled by default for the local mock demo.

Protected routes:

```text
POST   /api/risk/trading-enabled
POST   /api/risk/emergency-stop
DELETE /api/orders/{exchangeOrderId}
POST   /api/orders/local/{idOrClientOrderId}/cancel
POST   /api/orders/local/cancel-all
POST   /api/system/reconcile
POST   /api/system/reconcile/positions
GET    /api/integrations/tradingview/operations/deliveries
GET    /api/integrations/tradingview/operations/metrics
GET    /api/integrations/tradingview/operations/health
GET    /api/signals/{id}
GET    /api/orders/local/{idOrClientOrderId}
GET    /api/orders/local/{idOrClientOrderId}/history
GET    /api/system/reconciliation/status
```

Configuration:

```json
{
  "OperatorApi": {
    "Authentication": {
      "Enabled": true,
      "HeaderName": "X-TradeOps-Operator-Key",
      "ApiKey": "YOUR_OPERATOR_SECRET"
    }
  }
}
```

For Docker Compose:

```bash
export TRADEOPS_OPERATOR_AUTH_ENABLED=true
export TRADEOPS_OPERATOR_API_KEY='YOUR_OPERATOR_SECRET'
docker compose up --build -d
```

Health/readiness probes and read-only monitoring routes outside the paid-pilot evidence boundary remain accessible in this milestone. The operator and signal-ingress credentials are separate: neither credential implicitly authorizes the other boundary.

## Order lifecycle

Supported states:

```text
Created
Submitted
Accepted
PartiallyFilled
Filled
Cancelled
Rejected
Unknown
```

The state machine validates transitions and fill invariants. For example, `PartiallyFilled` requires:

```text
0 < FilledQuantity < RequestedQuantity
```

and `Filled` requires the entire requested quantity to be filled.

Bybit statuses are mapped into this exchange-neutral lifecycle. Unknown/unmapped exchange statuses become `Unknown` instead of being silently treated as a successful terminal state.

## Duplicate-order protection

For retries of the same logical signal, the caller must reuse the same `signalId`.

TradeOps derives a deterministic client order ID and relies on both:

1. an application-level lookup; and
2. a PostgreSQL unique index on `ClientOrderId`.

The current format is:

```text
trd-{Guid:N}
```

which is exactly 36 characters and therefore fits the Bybit `orderLinkId` limit.

The database constraint is the final guard against concurrent duplicate requests racing each other.

## PostgreSQL

For local non-Docker development, configure:

```text
ConnectionStrings__TradeOpsDb
```

Example:

```bash
export ConnectionStrings__TradeOpsDb='Host=localhost;Port=5432;Database=tradeops;Username=postgres;Password=YOUR_PASSWORD'
```

The initial EF Core migration is committed under:

```text
src/TradeOps.Infrastructure/Persistence/Migrations
```

Apply migrations manually when needed:

```bash
dotnet ef database update \
  --project src/TradeOps.Infrastructure \
  --startup-project src/TradeOps.Api
```

## Telegram alerts

Telegram is disabled by default. Never commit the bot token or chat id.

Docker Compose reads optional host environment variables:

```bash
export TELEGRAM_ENABLED=true
export TELEGRAM_BOT_TOKEN='YOUR_BOT_TOKEN'
export TELEGRAM_CHAT_ID='YOUR_CHAT_ID'

docker compose up --build -d
```

The adapter is fail-safe: Telegram delivery errors are logged but do not fail the order execution path.

## Worker settings

Defaults:

```json
{
  "Worker": {
    "ReconciliationIntervalSeconds": 30,
    "InitialRetryDelaySeconds": 2,
    "MaxRetryDelaySeconds": 30
  }
}
```

Retry delay grows exponentially until `MaxRetryDelaySeconds`.

## Local .NET development

Build:

```bash
dotnet restore TradeOps.sln
dotnet build TradeOps.sln
```

Run API:

```bash
dotnet run --project src/TradeOps.Api
```

Run Worker in another terminal using the same PostgreSQL connection string:

```bash
dotnet run --project src/TradeOps.Worker
```

To run locally against Bybit testnet instead of the mock, provide the standard .NET environment variables:

```bash
export Exchange__Provider=BybitTestnet
export Exchange__Bybit__ApiKey='YOUR_TESTNET_KEY'
export Exchange__Bybit__ApiSecret='YOUR_TESTNET_SECRET'
```

## CI

GitHub Actions runs on `main` and `TradeOps/**` branches. The default pipeline remains credential-free and uses `MockExchangeClient`.

It:

1. restores and builds the complete .NET 8 solution;
2. runs the unit/integration test suite, including signal authentication, HMAC body binding, persistent replay protection and operator API authentication coverage;
3. starts PostgreSQL 16 and the API;
4. verifies liveness/readiness and the OpenAPI surface;
5. exercises signal execution, idempotent retry, reconciliation and order history;
6. verifies local cancellation, bulk cancellation and persistent emergency-stop behavior;
7. exercises fills, daily P&L, operational run status and execution metrics;
8. runs the signed webhook end-to-end demo against a secured Mock API instance;
9. runs the isolated customer TradingView demo and generates/validates the paid-pilot evidence package against the same persisted event;
10. validates the client-pilot starter kit, rendered TradingView sample and merged Compose contract;
11. validates Docker Compose and the TradingView Nginx gateway configuration;
12. builds the API/Worker images.

Real Bybit testnet credentials are intentionally not required by ordinary CI.

## Safety boundary

- mock mode is the default;
- the Bybit implementation accepts only the official testnet host in this branch;
- no API key or secret is stored in the repository;
- optional external signal-ingress authentication uses configuration/environment secrets only;
- optional persistent replay protection rejects stale timestamps and repeated request IDs before execution;
- optional HMAC-SHA256 signing cryptographically binds replay metadata, HTTP route and exact request body;
- authenticated replay-protected attempts are persisted as correlation audit records without storing request bodies or secrets;
- the optional TradingView adapter normalizes provider payloads behind a separate trusted-gateway boundary without changing the signed core ingress;
- the v1.2.0.1 gateway deployment terminates HTTPS on port 443, filters TradingView source IPs and client-certificate identity, and injects the internal adapter credential;
- TradingView authenticated delivery attempts are persisted with provider outcome, HTTP result, latency and signal/order correlation without storing alert bodies or gateway secrets;
- optional TradingView delivery health monitoring is observational only: it persists health state and sends transition alerts but never changes trading controls, cancels orders or bypasses risk decisions;
- mutating operator actions can use a separate configuration/environment credential;
- no secret/signing payload is logged;
- an ambiguous placement outcome is reconciled by deterministic client order ID instead of blindly resubmitting;
- real-money/mainnet trading remains out of scope for the current public version.
