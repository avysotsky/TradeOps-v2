# Operator API authentication

TradeOps can protect mutating operator actions with a credential that is independent from the external signal-ingress key.

## Protected actions

When operator authentication is enabled, the following routes require the operator API key:

```text
POST   /api/risk/trading-enabled
POST   /api/risk/emergency-stop
DELETE /api/orders/{exchangeOrderId}
POST   /api/orders/local/{idOrClientOrderId}/cancel
POST   /api/orders/local/cancel-all
POST   /api/system/reconcile
POST   /api/system/reconcile/positions
```

Read-only monitoring routes, health endpoints and Swagger remain unchanged in this milestone.

## Configuration

```text
OperatorApi__Authentication__Enabled=true
OperatorApi__Authentication__HeaderName=X-TradeOps-Operator-Key
OperatorApi__Authentication__ApiKey=<secret>
```

The feature is disabled by default for the local mock demo. When enabled, startup validation requires a non-empty header name and API key.

The operator key is intentionally separate from `SignalIngress:Authentication`. A signal-ingress credential does not authorize operator actions, and an operator credential does not replace the signal-ingress credential.

## Request behavior

Missing, duplicated or incorrect operator credentials return HTTP 401 before the controller action runs. Comparison uses SHA-256 plus `CryptographicOperations.FixedTimeEquals`; secrets are not logged or returned.

Use this mechanism only over TLS. It is a narrow credential boundary for the current demo/testnet execution backend, not a complete user identity, role or permission system.
