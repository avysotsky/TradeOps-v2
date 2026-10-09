# TradeOps exchange expansion plan

## Goal

Expand TradeOps from the current Mock + Bybit Testnet model into a reusable multi-exchange execution backend while preserving the existing safety properties:

- deterministic client order identity;
- no blind retry after ambiguous placement;
- exchange reconciliation;
- persistent lifecycle/audit;
- operator controls;
- test-first rollout before real-money execution.

## Target exchanges

### 1. Binance USD-M Futures Testnet

First implementation target.

Reasons:
- mature REST/WebSocket API;
- supported Futures Testnet;
- direct fit for the existing `IExchangeClient` / `IExchangeEventStream` architecture;
- useful commercial coverage for crypto execution clients.

Planned scope:

1. REST authentication/signing;
2. account/balance;
3. positions;
4. open orders;
5. order lookup by exchange/client ID with symbol context;
6. market/limit order placement;
7. cancellation;
8. instrument filters;
9. private order/execution stream;
10. read-only smoke + deterministic testnet E2E.

Provider name:

```text
BinanceFuturesTestnet
```

Mainnet remains disabled.

### 2. Hyperliquid Testnet

Second implementation target.

Hyperliquid has an explicit testnet API endpoint and testnet trading flow.

Planned scope:

1. wallet/API signing;
2. metadata/asset-id resolution;
3. account state;
4. open positions;
5. open orders;
6. place/cancel order;
7. order/fill lifecycle;
8. WebSocket subscriptions;
9. reconciliation;
10. testnet E2E.

Provider name:

```text
HyperliquidTestnet
```

Mainnet remains disabled.

### 3. MEXC Futures

Third implementation target.

MEXC Futures API trading is supported, including .NET integration, but MEXC currently does not provide a sandbox/test environment. Its API connects directly to live trading.

Therefore the safe rollout is different:

1. implement transport/signing/contracts;
2. implement public/read-only/private-account reads;
3. contract-test request signing and response mapping;
4. keep order placement/cancellation disabled by default;
5. only enable live execution in a separate explicitly approved milestone.

Provider names should distinguish safe/read-only and any future live mode. Do not silently map a generic `MEXC` provider to real-money order placement.

## Contract change required before Binance

Some exchanges require a symbol when querying or cancelling an order.

TradeOps originally exposed:

```text
GetOrderAsync(exchangeOrderId)
GetOrderByClientOrderIdAsync(clientOrderId)
CancelOrderAsync(exchangeOrderId)
```

v1.4.0.0 adds symbol-aware overloads:

```text
GetOrderAsync(exchangeOrderId, symbol)
GetOrderByClientOrderIdAsync(clientOrderId, symbol)
CancelOrderAsync(exchangeOrderId, symbol)
```

Existing adapters remain source-compatible through default interface methods, while new exchange adapters can require symbol context.

Application services must pass the persisted local order symbol whenever it is known.

## Delivery sequence

```text
v1.4.0.0  Multi-Exchange Order Context
v1.4.1.x  Binance Futures Testnet REST
v1.4.2.x  Binance Futures Testnet private stream + E2E
v1.5.x    Hyperliquid Testnet
v1.6.x    MEXC safe/read-only adapter
future    explicit live-execution milestone only after separate approval
```

Do not implement all three venues in one PR.
