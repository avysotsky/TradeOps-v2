# Operational risk controls

TradeOps persists operational trading controls in PostgreSQL. They survive API/Worker restarts and are evaluated before any new order is sent to the exchange.

## Persistent state

The singleton `OperationalRiskState` contains:

- `TradingEnabled`;
- `EmergencyStop`;
- `EmergencyStopReason`;
- `UpdatedAt`.

`RiskSettings.TradingEnabled` and `RiskSettings.EmergencyStop` are used only as bootstrap defaults when the singleton row is created for the first time.

## Risk snapshot

`GET /api/risk` returns the configured limits together with the current persistent controls, fill-derived UTC daily realized PnL, and the number of active `PositionMismatch` events.

Daily realized PnL is calculated as cumulative realized PnL now minus cumulative realized PnL at the start of the current UTC day. This correctly attributes a close performed today even when the position was opened on a previous day. It is currently gross PnL; fee-currency normalization remains a separate concern.

## Commands

Enable or disable new trading:

```http
POST /api/risk/trading-enabled
Content-Type: application/json

{ "enabled": false }
```

Activate emergency stop:

```http
POST /api/risk/emergency-stop
Content-Type: application/json

{
  "enabled": true,
  "reason": "operator incident response"
}
```

Clear emergency stop:

```http
POST /api/risk/emergency-stop
Content-Type: application/json

{ "enabled": false }
```

## Fail-closed behavior

A signal is rejected before exchange position lookup when any of the following applies:

- persistent trading is disabled;
- emergency stop is active;
- one or more active `PositionMismatch` events exist;
- the UTC daily realized loss limit has been reached;
- the symbol or requested quantity violates static risk settings.

Exchange-dependent position-size checks run only after these local/persistent checks pass.

Every rejected signal is persisted as a resolved `RiskRejected` audit event. `OrderManager` continues to emit its existing risk-rejection alert, so the execution/idempotency pipeline does not need a separate risk-event dependency.

## Position mismatch policy

Any active `PositionMismatch` blocks all new orders. This is intentionally conservative: when local fill-derived exposure disagrees with the exchange, TradeOps treats execution state as unreliable until reconciliation resolves the mismatch.

Risk-event history is available through:

```http
GET /api/risk/events?limit=100
```
