# HMAC-signed signal ingress

TradeOps can cryptographically bind an authenticated signal request to its timestamp, request ID, HTTP method, request path and exact request body.

Signing is optional and disabled by default. When enabled, it requires both signal-ingress API-key authentication and replay protection.

## Headers

A signed request to `POST /api/signals` includes:

```text
X-TradeOps-Api-Key: <API key>
X-TradeOps-Timestamp: <Unix timestamp in seconds>
X-TradeOps-Request-Id: <GUID>
X-TradeOps-Signature: sha256=<64 lowercase/uppercase hex characters>
```

## Canonical payload

The HMAC-SHA256 input is the UTF-8 encoding of this prefix followed immediately by the exact raw HTTP request-body bytes:

```text
<unixTimestamp>\n
<requestId in canonical D format>\n
<UPPERCASE HTTP method>\n
<request path>\n
<raw request body bytes>
```

For the current endpoint the method/path pair is:

```text
POST
/api/signals
```

The timestamp is canonicalized from the parsed Unix-seconds integer. The request ID is canonicalized as the standard hyphenated GUID `D` format.

The signature is:

```text
HMAC-SHA256(signingSecret, canonicalPayload)
```

and is transmitted as:

```text
sha256=<hex digest>
```

Do not parse and reserialize JSON between signing and sending. The exact body bytes received by TradeOps are part of the signature.

## Validation order

TradeOps performs the security checks in this order:

```text
API key
  -> timestamp/request-id syntax and freshness
  -> HMAC signature
  -> persistent request-id registration
  -> model validation / risk / execution
```

An invalid signature does not consume the request ID. This prevents unauthenticated or tampered requests from reserving a valid nonce.

## Configuration

```json
{
  "SignalIngress": {
    "Authentication": {
      "Enabled": true,
      "HeaderName": "X-TradeOps-Api-Key",
      "ApiKey": "YOUR_API_KEY"
    },
    "ReplayProtection": {
      "Enabled": true,
      "TimestampHeaderName": "X-TradeOps-Timestamp",
      "RequestIdHeaderName": "X-TradeOps-Request-Id",
      "AllowedClockSkewSeconds": 300,
      "ReceiptRetentionSeconds": 600
    },
    "Signing": {
      "Enabled": true,
      "SignatureHeaderName": "X-TradeOps-Signature",
      "Secret": "A_SEPARATE_SIGNING_SECRET_AT_LEAST_32_CHARACTERS",
      "MaxBodyBytes": 65536
    }
  }
}
```

For Docker Compose:

```bash
export TRADEOPS_SIGNAL_AUTH_ENABLED=true
export TRADEOPS_SIGNAL_API_KEY='YOUR_API_KEY'
export TRADEOPS_SIGNAL_REPLAY_ENABLED=true
export TRADEOPS_SIGNAL_SIGNING_ENABLED=true
export TRADEOPS_SIGNAL_SIGNING_SECRET='A_SEPARATE_SIGNING_SECRET_AT_LEAST_32_CHARACTERS'
docker compose up --build -d
```

Keep the API key and HMAC signing secret separate. Use the endpoint only over TLS.

## Failure behavior

- invalid API key: HTTP 401;
- malformed replay metadata: HTTP 400;
- stale timestamp: HTTP 401;
- missing/malformed/wrong HMAC signature: HTTP 401;
- signed body larger than `MaxBodyBytes`: HTTP 413;
- reused valid request ID: HTTP 409.

Signature failures occur before PostgreSQL nonce registration and before signal validation, risk evaluation or exchange execution.
