# Signal ingress authentication

TradeOps can protect external signal submission with a shared API key while leaving the execution model unchanged.

## Scope

The authentication gate applies only to:

```text
POST /api/signals
```

It does not add strategy logic, signal generation, mainnet support or a second venue.

## Configuration

```text
SignalIngress__Authentication__Enabled=true
SignalIngress__Authentication__HeaderName=X-TradeOps-Api-Key
SignalIngress__Authentication__ApiKey=<secret>
```

Authentication is disabled by default for the local mock demo. When enabled, startup validation requires a non-empty header name and API key.

## Request behavior

A missing, duplicated or incorrect API-key header is rejected with HTTP 401 before model validation, database persistence, risk evaluation or exchange execution.

The middleware hashes the configured and supplied values with SHA-256 and compares the hashes with `CryptographicOperations.FixedTimeEquals`. The secret is never logged or returned.

This mechanism is intended to be used over TLS. It is a narrow ingress-control feature for external execution requests; it is not a complete identity/authorization system for every operator endpoint.
