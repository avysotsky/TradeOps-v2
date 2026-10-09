# Signed webhook end-to-end demo

`TradeOps.SignedWebhookDemo` is a .NET 8 console client that exercises the commercial webhook-to-execution flow against a running TradeOps API.

It does not generate trading logic or signals. It sends a fixed-format demo instruction to the existing execution backend.

## What the demo proves

The client performs the following sequence:

```text
health/readiness
  -> create JSON signal bytes
  -> timestamp + request ID
  -> HMAC-SHA256 signature
  -> tampered body with original signature -> HTTP 401
  -> same request ID with correct body/signature -> HTTP 200
  -> exact replay -> HTTP 409
  -> read signal audit / deterministic client order ID
  -> operator-authenticated reconciliation
  -> verify lifecycle ends in Filled
  -> query execution metrics
```

The tamper check intentionally runs before the valid request and reuses the same request ID. This demonstrates that invalid HMAC attempts do not consume the replay nonce.

## Start TradeOps in secured mock mode

For a deterministic presentation, start from a clean demo database/volume so existing risk or reconciliation state from earlier experiments cannot intentionally block a new signal:

```bash
docker compose down -v
```

Set demo-only credentials in your shell. Use different secrets outside a local demo.

```bash
export TRADEOPS_SIGNAL_AUTH_ENABLED=true
export TRADEOPS_SIGNAL_API_KEY='local-demo-signal-api-key'
export TRADEOPS_SIGNAL_REPLAY_ENABLED=true
export TRADEOPS_SIGNAL_SIGNING_ENABLED=true
export TRADEOPS_SIGNAL_SIGNING_SECRET='local-demo-signing-secret-at-least-32-characters'
export TRADEOPS_OPERATOR_AUTH_ENABLED=true
export TRADEOPS_OPERATOR_API_KEY='local-demo-operator-api-key'

docker compose up --build -d
```

## Run the .NET client

```bash
export TRADEOPS_DEMO_API_URL='http://localhost:8080'
export TRADEOPS_DEMO_SIGNAL_API_KEY='local-demo-signal-api-key'
export TRADEOPS_DEMO_SIGNING_SECRET='local-demo-signing-secret-at-least-32-characters'
export TRADEOPS_DEMO_OPERATOR_API_KEY='local-demo-operator-api-key'

dotnet run --project tools/TradeOps.SignedWebhookDemo
```

Expected final line:

```text
SIGNED WEBHOOK E2E DEMO: PASS
```

## Boundaries

- default exchange path remains Mock;
- Bybit remains testnet-only elsewhere in the repository;
- this demo does not enable mainnet or real-money trading;
- the demo client supplies execution instructions but no alpha, signal generation or strategy logic;
- API key, signing secret and operator key are supplied at runtime and are not committed to the repository.
