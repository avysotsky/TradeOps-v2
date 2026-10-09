# Signal ingress replay protection

TradeOps can reject stale or repeated authenticated requests at `POST /api/signals`.

Replay protection is optional and is disabled by default for the local mock demo. It can only be enabled when signal-ingress API-key authentication is enabled.

## Request headers

When enabled, every signal submission must include:

```text
X-TradeOps-Api-Key: <shared secret>
X-TradeOps-Timestamp: <Unix timestamp in seconds>
X-TradeOps-Request-Id: <GUID>
```

The timestamp must be within the configured clock-skew window. The request ID is registered in PostgreSQL before signal validation, persistence, risk evaluation or exchange execution.

A request ID is therefore shared across API instances and survives process restarts while its receipt is retained.

## Configuration

```json
{
  "SignalIngress": {
    "Authentication": {
      "Enabled": true,
      "HeaderName": "X-TradeOps-Api-Key",
      "ApiKey": "YOUR_SECRET"
    },
    "ReplayProtection": {
      "Enabled": true,
      "TimestampHeaderName": "X-TradeOps-Timestamp",
      "RequestIdHeaderName": "X-TradeOps-Request-Id",
      "AllowedClockSkewSeconds": 300,
      "ReceiptRetentionSeconds": 600
    }
  }
}
```

`ReceiptRetentionSeconds` must be at least twice `AllowedClockSkewSeconds`. This covers the full acceptance interval for a request timestamp that is initially at the future edge of the allowed clock-skew window.

Expired receipts are pruned opportunistically as new authenticated requests are registered.

## Responses

- missing or malformed replay headers: HTTP 400;
- timestamp outside the allowed window: HTTP 401;
- already-registered request ID: HTTP 409;
- valid fresh request: continues to the existing signal validation/execution pipeline.

The request ID and timestamp are not yet cryptographically bound to the request body. This milestone prevents exact request-ID replay and accidental duplicate delivery. A future signed-webhook milestone will bind the metadata and body together with HMAC.

Use the ingress only over TLS.
