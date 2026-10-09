# TradingView delivery health and alerting

TradeOps v1.2.1.1 adds stateful health evaluation for TradingView delivery operations.

The evaluator runs in the Worker process, uses persisted delivery audit data, stores the latest health state in PostgreSQL and reuses the existing fail-safe `IAlertService` / Telegram adapter.

## Health states

```text
Unknown
InsufficientData
Healthy
Degraded
Critical
```

The evaluator uses a configurable lookback window.

Rate-based checks do not activate until `MinimumDeliveries` has been reached. This prevents a single isolated delivery from generating a percentage-based incident.

## Default thresholds

```json
{
  "TradingViewHealth": {
    "Enabled": false,
    "EvaluationIntervalSeconds": 60,
    "LookbackMinutes": 15,
    "MinimumDeliveries": 5,
    "CriticalFailureCount": 2,
    "CriticalFailureRatePercent": 20,
    "DegradedConflictRatePercent": 20,
    "DegradedRiskRejectedRatePercent": 50,
    "MaxAverageLatencyMilliseconds": 1000,
    "NoSuccessfulDeliveryMinutes": 0
  }
}
```

`NoSuccessfulDeliveryMinutes = 0` disables inactivity/stale-success alerting by default. Enable it only when the deployment is expected to receive successful TradingView events continuously enough for that assumption to be meaningful.

## Severity

`Critical` is entered when:

- the failure count reaches the configured critical count; or
- the failed-delivery rate reaches the critical percentage; or
- stale/no-success monitoring is enabled and its threshold is exceeded.

`Degraded` is entered when the configured conflict rate, risk-rejection rate or average-latency threshold is exceeded.

Otherwise, after the minimum sample size is reached, the status is `Healthy`.

## Alert suppression

Alerts are transition-based, not delivery-based.

Examples:

```text
Healthy -> Degraded   => one Warning
Degraded -> Degraded => no repeated alert
Degraded -> Critical => one Critical
Critical -> Healthy  => one recovery Info
```

The last health state is stored in PostgreSQL, so restarting the Worker does not by itself generate another alert for an unchanged condition.

## Read-only health endpoint

```text
GET /api/integrations/tradingview/operations/health
```

It returns the last persisted evaluation. Before the Worker has evaluated health for the first time, the endpoint returns HTTP 404.

## Boundaries

Health monitoring does not:

- disable trading;
- trigger emergency stop;
- cancel orders;
- retry TradingView deliveries;
- modify risk-engine decisions.

It is observability and alerting only.
