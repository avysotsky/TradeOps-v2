# Customer TradingView demo

TradeOps v1.3.0.0 packages the TradingView integration into a repeatable local customer demo.

The demo is intentionally **Mock-only**. It demonstrates execution engineering and operational controls without using real money or Bybit mainnet.

## What the customer sees

```text
TradingView-style event
  -> authenticated provider adapter
  -> canonical TradingSignal
  -> deterministic ClientOrderId
  -> Mock execution
  -> provider redelivery without duplicate order
  -> conflicting event-id reuse rejected
  -> delivery audit and correlation
  -> operator-authenticated reconciliation
  -> final Filled lifecycle
  -> provider metrics
  -> Worker health evaluation
```

The demo proves the engineering path. It does not claim to provide a profitable strategy or trading edge.

## Requirements

- Docker / Docker Compose v2;
- .NET 8 SDK;
- curl;
- ports 8080 and 54321 available locally.

## One-command run

From the repository root:

```bash
bash scripts/tradingview-customer-demo.sh
```

The script uses explicit local-only demo credentials when environment variables are not supplied. They are not production secrets.

The final output should contain:

```text
CUSTOMER TRADINGVIEW DEMO: PASS
```

and a correlation line:

```text
eventId -> signalId -> clientOrderId
```

## Demo-specific health thresholds

The Compose overlay enables TradingView health monitoring with a 15-second evaluation interval and a minimum sample of three provider deliveries.

The scenario deliberately creates one `Conflict` to prove event-id protection. Therefore the demo conflict threshold is 50%, not the default production-like 20%. This is a demo fixture, not a recommended production threshold.

## Inspect after the run

The stack remains running so the customer can inspect Swagger and the read-only operator endpoints.

Useful routes:

```text
GET /swagger
GET /api/integrations/tradingview/operations/deliveries
GET /api/integrations/tradingview/operations/metrics
GET /api/integrations/tradingview/operations/health
GET /api/signals
GET /api/orders/local
```

Stop the stack:

```bash
docker compose \
  -f docker-compose.yml \
  -f deploy/customer-demo/docker-compose.tradingview-demo.yml \
  down
```

Delete demo PostgreSQL data too:

```bash
docker compose \
  -f docker-compose.yml \
  -f deploy/customer-demo/docker-compose.tradingview-demo.yml \
  down -v
```

## Production boundary

This customer demo calls the internal TradingView adapter on localhost using the gateway credential.

It does **not** replace the production edge. Public TradingView deployment continues to use the separate Nginx HTTPS gateway under:

```text
deploy/tradingview-gateway/
```

with source-IP allowlisting, client-certificate identity checks and internal credential injection.
