# Trusted TradingView gateway

This deployment layer is for TradeOps v1.2.0.1.

It terminates public HTTPS for TradingView and forwards only accepted webhook traffic to the internal TradeOps TradingView adapter.

## Security model

The gateway requires both:

1. the request source IP to match TradingView's published webhook sender list;
2. an HTTPS client certificate whose subject contains `CN=webhook-server@tradingview.com`.

Only then does the gateway inject the internal `X-TradeOps-TradingView-Gateway-Key` credential and proxy the request to:

```text
http://api:8080/api/integrations/tradingview
```

The public caller cannot choose the internal credential because Nginx overwrites that header.

The gateway does not store the alert body or gateway secret in access logs.

## Important certificate-verification detail

TradingView documents the client-certificate identity fields but does not publish a dedicated webhook CA bundle in its webhook-authentication documentation.

For that reason this Nginx template uses `ssl_verify_client optional_no_ca`, then requires the documented TradingView certificate subject **and** the published TradingView source-IP allowlist.

This is intentionally a two-factor network/TLS-origin check, not certificate-chain pinning.

Do not remove the source-IP allowlist unless you replace it with another cryptographically authenticated edge.

## Official TradingView webhook source IPs

The template currently allows:

```text
52.89.214.238
34.212.75.30
54.218.53.128
52.32.178.7
```

Before production deployment, compare this list with TradingView's current webhook documentation.

## Prerequisites

You need:

- a public DNS name pointing to the gateway host;
- TCP 443 reachable from TradingView;
- a valid public TLS server certificate for that DNS name;
- Docker Compose v2;
- a strong random internal gateway key.

The TradeOps API itself should not be exposed to untrusted networks. If port 8080 is published by the base Compose file, restrict it with the host firewall/security group to localhost or a trusted administration network.

## Start

Set absolute paths to the TLS certificate and private key:

```bash
export TRADEOPS_TRADINGVIEW_GATEWAY_KEY='A_RANDOM_INTERNAL_SECRET_AT_LEAST_32_CHARACTERS'
export TRADEOPS_TRADINGVIEW_TLS_CERT='/etc/letsencrypt/live/tradeops.example.com/fullchain.pem'
export TRADEOPS_TRADINGVIEW_TLS_KEY='/etc/letsencrypt/live/tradeops.example.com/privkey.pem'
```

Then start the normal stack plus the gateway overlay:

```bash
docker compose \
  -f docker-compose.yml \
  -f deploy/tradingview-gateway/docker-compose.tradingview-gateway.yml \
  up --build -d
```

The public TradingView webhook URL is:

```text
https://tradeops.example.com/webhooks/tradingview
```

Do not put the gateway key, exchange keys, logins, or passwords into the TradingView webhook URL or alert body.

## Health check

The gateway exposes:

```text
GET https://tradeops.example.com/healthz
```

This endpoint does not require the TradingView client certificate and does not proxy to TradeOps.

## Request path

```text
TradingView
  |
  | HTTPS :443
  | source-IP allowlist
  | TradingView client certificate subject
  v
Nginx gateway
  |
  | inject internal gateway key
  v
TradeOps /api/integrations/tradingview
  |
  v
canonical TradingSignal
  |
  v
RiskEngine -> execution -> persistence -> reconciliation
```

## Time budget

TradingView currently cancels a webhook if the remote server takes longer than about three seconds.

The gateway therefore uses short upstream timeouts. A delivery may be retried after a timeout or server error, so the TradingView `eventId` must remain stable for the same logical event. TradeOps converts `eventId` to a deterministic signal ID, making a redelivery idempotent.

## Network boundary

The adapter-side `RequireGatewayIpAllowlist` is disabled in this Docker overlay because the adapter sees the private Docker source address of Nginx, not TradingView's public address. The public IP validation is performed at Nginx before the internal credential is injected.

This design assumes the Docker network between gateway and API is trusted. The gateway key remains required by the TradeOps adapter.
