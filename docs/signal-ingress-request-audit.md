# Signal ingress request audit and correlation

TradeOps persists an operational audit record for each authenticated webhook attempt that reaches a syntactically valid replay identity.

The audit is designed to answer:

> What happened to webhook request ID `X`?

## Correlation chain

```text
requestId
  -> ingress attempt
  -> signalId
  -> local orderId / ClientOrderId
  -> signal audit / order lifecycle / fills / reconciliation
```

A request ID can have multiple ingress attempts. For example, a valid signed request may be followed by an exact replay. Those are stored as separate audit rows rather than overwriting the original successful attempt.

## Stored fields

Each attempt stores only operational metadata:

- audit ID;
- request ID;
- request timestamp;
- received/completed timestamps;
- HTTP method/path;
- outcome and HTTP status;
- signal ID when the request reached signal processing;
- local order ID / deterministic client order ID when execution produced an order.

TradeOps does **not** persist the request body, API key, HMAC signature or signing secret in this audit.

## Outcomes

Current outcomes are:

```text
Received
TimestampRejected
SignatureRejected
PayloadRejected
ReplayRejected
RequestRejected
SignalConflict
RiskRejected
Accepted
Failed
```

`Received` is the transient/pending state. Completed requests are finalized to another outcome.

API-key failures are intentionally not written to this correlation table because they occur before TradeOps accepts the supplied request identity as authenticated. Requests with malformed replay metadata may also lack a trustworthy request ID and therefore do not have a correlation record.

## Operator lookup

```text
GET /api/signal-ingress/requests/{requestId}
```

The endpoint returns all recorded attempts in receive order. A response also exposes `X-TradeOps-Ingress-Audit-Id` for requests whose audit row has been created.

This endpoint is read-only and follows the current demo policy for read-only operator endpoints.
