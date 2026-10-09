# TradingView webhook adapter

TradeOps v1.2.0.0 adds a dedicated TradingView adapter:

```text
POST /api/integrations/tradingview
```

The adapter is disabled by default.

## Why it is separate from /api/signals

The core `POST /api/signals` endpoint uses API-key authentication, timestamp/request-ID replay protection and optional HMAC-SHA256 body signing.

TradingView webhooks send an HTTP POST whose alert message becomes the request body. TradeOps therefore does not weaken the signed core ingress to accommodate provider-specific limitations.

The recommended deployment boundary is:

```text
TradingView
  -> HTTPS gateway / reverse proxy
       - verify TradingView origin at the TLS/network edge
       - inject internal gateway credential
  -> TradeOps /api/integrations/tradingview
  -> normalize to TradeOps TradingSignal
  -> existing risk / execution / persistence / reconciliation
```

TradeOps v1.2.0.1 includes a concrete Nginx deployment for that boundary under `deploy/tradingview-gateway/`.

The TradeOps adapter validates a separate internal gateway key. Optionally it also checks the direct gateway source IP.

Do not expose the adapter publicly with the gateway credential reachable from untrusted networks. Keep TradeOps network-isolated behind the trusted gateway.

## Configuration

```json
{
  "Integrations": {
    "TradingView": {
      "Enabled": true,
      "GatewayHeaderName": "X-TradeOps-TradingView-Gateway-Key",
      "GatewayKey": "A_SEPARATE_INTERNAL_SECRET_AT_LEAST_32_CHARACTERS",
      "RequireGatewayIpAllowlist": true,
      "AllowedGatewayIps": [
        "127.0.0.1",
        "::1"
      ]
    }
  }
}
```

Docker Compose environment variables:

```bash
export TRADEOPS_TRADINGVIEW_ENABLED=true
export TRADEOPS_TRADINGVIEW_GATEWAY_KEY='A_SEPARATE_INTERNAL_SECRET_AT_LEAST_32_CHARACTERS'
```

The gateway key is an internal gateway-to-TradeOps credential. It is not meant to be embedded in a TradingView URL or alert body.

## Payload

```json
{
  "eventId": "BTCUSDT|1h|2026-10-04T15:00:00Z|long-entry",
  "symbol": "BTCUSDT",
  "action": "buy",
  "quantity": 0.001,
  "riskPercent": 1.0,
  "stopLoss": 60000,
  "takeProfit": 65000
}
```

`action` accepts `buy` or `sell`.

`eventId` is required and must be stable for all deliveries of the same TradingView event. TradeOps deterministically converts it into the signal ID. Re-delivery of the same event with the same execution identity therefore reuses the existing signal/order instead of creating a duplicate. Reusing an event ID with conflicting execution fields returns HTTP 409.

The adapter stores the signal source/type as `TradingView`.

## Example TradingView strategy message

TradingView strategy placeholders can populate a JSON alert message such as:

```json
{
  "eventId": "{{timenow}}|{{ticker}}|{{strategy.order.id}}|{{strategy.order.action}}|{{strategy.order.contracts}}",
  "symbol": "{{ticker}}",
  "action": "{{strategy.order.action}}",
  "quantity": {{strategy.order.contracts}}
}
```

The exact event-ID template is the customer's responsibility. It must be unique enough for distinct intended executions while remaining identical if TradingView redelivers the same event.

## Trusted gateway deployment

The included v1.2.0.1 gateway:

- listens on HTTPS port 443;
- accepts only TradingView's currently published webhook source IP addresses;
- requires a presented client certificate whose subject contains `CN=webhook-server@tradingview.com`;
- overwrites `X-TradeOps-TradingView-Gateway-Key` with the internal secret rather than trusting a caller-supplied value;
- proxies only `/webhooks/tradingview` to the internal adapter;
- exposes a separate `/healthz` endpoint;
- limits request bodies to 64 KiB and applies short upstream timeouts.

TradingView documents client-certificate identity fields but does not publish a dedicated webhook CA bundle in the same documentation. The supplied stock-Nginx template therefore combines certificate-subject verification with TradingView's published source-IP allowlist instead of claiming certificate-chain pinning.

Deployment instructions are in `deploy/tradingview-gateway/README.md`.

## Operational constraints

Keep the adapter response path fast. TradingView webhook delivery has a short response deadline and can redeliver server-error responses. TradeOps idempotency based on `eventId` is intended to make such redelivery safe.

This adapter supplies execution instructions only. It does not create alpha, trading signals or strategy logic.
