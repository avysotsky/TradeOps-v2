# Paid pilot evidence and handoff

TradeOps v1.3.2.1 uses the read-only evidence exporter for completed TradingView pilot events.

It consumes the existing audit/operations API and creates:

```text
pilot-evidence/
├── pilot-evidence.json
└── pilot-evidence.md
```

No order is placed, cancelled or reconciled by the exporter.

## Required input

```bash
export TRADEOPS_PILOT_API_URL='http://localhost:8080'
export TRADEOPS_PILOT_EVENT_ID='THE_APPROVED_TRADINGVIEW_EVENT_ID'
export TRADEOPS_OPERATOR_API_KEY='THE_CONFIGURED_OPERATOR_KEY'
```

Optional report metadata:

```bash
export TRADEOPS_PILOT_CLIENT='Client name'
export TRADEOPS_PILOT_STAGE='Mock'
export TRADEOPS_PILOT_VERSION='v1.3.2.1'
export TRADEOPS_PILOT_OUTPUT_DIR='pilot-evidence'
```

Generate the package:

```bash
dotnet run --project tools/TradeOps.PilotEvidence
```

A technically accepted pilot prints:

```text
PILOT EVIDENCE: PASS
```

## Collected evidence

For the supplied TradingView `eventId`, the exporter captures:

- all retained provider delivery attempts;
- delivery outcomes, HTTP results and latency;
- canonical `SignalId`;
- canonical `ClientOrderId` and local order;
- signal details and current outcome;
- full local order lifecycle history;
- TradingView delivery metrics around the event;
- latest TradingView health state;
- latest order-reconciliation status.

The report calculates technical checks for:

- accepted provider delivery;
- idempotent redelivery;
- conflicting event-ID protection;
- one canonical signal/order correlation;
- lifecycle evidence;
- successful reconciliation evidence;
- delivery metrics;
- non-critical health snapshot.

## Report boundary

A technical `PASS` means the configured pilot evidence satisfies the engineering acceptance checks.

It does **not** certify profitability, alpha, strategy quality, future returns or mainnet readiness.

The Markdown report includes a separate client sign-off section because automated technical evidence does not replace customer acceptance.

## Security

The exporter calls read-only endpoints only.

When operator API authentication is enabled, it sends the configured operator credential using `X-TradeOps-Operator-Key`. A custom header name can be supplied with `TRADEOPS_OPERATOR_API_HEADER`.

The exporter does not place, cancel or reconcile orders and does not require gateway, exchange or Telegram credentials.

Generated evidence may contain symbols, quantities, timestamps and operational identifiers. Treat it as client project material rather than a public artifact unless the client has approved disclosure.
