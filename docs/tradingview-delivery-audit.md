# TradingView delivery audit and operations

TradeOps v1.2.1.0 adds provider-level delivery observability for the TradingView adapter.

Each authenticated TradingView delivery that reaches the adapter action creates a separate audit row.

## What is recorded

The audit stores:

- TradeOps delivery ID;
- TradingView `eventId` when supplied;
- receive/completion timestamps;
- processing latency in milliseconds;
- provider-delivery outcome;
- HTTP status returned by the adapter;
- `SignalId`, local `OrderId` and deterministic `ClientOrderId` when available;
- normalized symbol/action metadata.

The audit does **not** persist:

- the full TradingView alert body;
- the internal gateway key;
- TLS client-certificate material;
- exchange credentials.

## Outcomes

```text
Received
ValidationRejected
Accepted
Redelivered
RiskRejected
Conflict
Failed
```

`Received` is the pending state.

`Redelivered` means the same stable TradingView `eventId` already mapped to an existing TradeOps signal/order and the idempotent execution path returned that existing state rather than creating another order.

## Lookup

Recent deliveries:

```text
GET /api/integrations/tradingview/operations/deliveries?limit=50
```

All retained deliveries for one TradingView event:

```text
GET /api/integrations/tradingview/operations/deliveries?eventId=<eventId>&limit=200
```

The webhook POST response also includes:

```text
X-TradeOps-TradingView-Delivery-Id: <GUID>
```

after an audit row has been created.

## Metrics

Default 24-hour window:

```text
GET /api/integrations/tradingview/operations/metrics
```

Explicit window:

```text
GET /api/integrations/tradingview/operations/metrics?from=<ISO-8601>&to=<ISO-8601>
```

The maximum query window is 31 days.

Metrics include:

- total and pending deliveries;
- accepted deliveries;
- idempotent redeliveries;
- validation/risk rejections;
- conflicts/failures;
- average and maximum adapter processing latency.

## Security boundary

Gateway authorization still happens before MVC model binding. Requests rejected by the TradingView gateway authorization filter are intentionally not written to this provider audit because TradeOps has not accepted their payload or `eventId` as trusted provider input.

Malformed JSON rejected by ASP.NET Core before the controller action similarly has no provider-delivery record.

The operations GET endpoints follow TradeOps' current read-only monitoring policy. Mutating operator actions remain separately protected.
