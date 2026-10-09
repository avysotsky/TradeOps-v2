# Client pilot acceptance criteria

Use this checklist for a first paid TradingView integration pilot.

The pilot is accepted only for execution engineering. It does not certify profitability or strategy quality.

## A. Security and deployment

- [ ] Public TradingView traffic enters through HTTPS on the trusted gateway.
- [ ] The TradeOps API is not intentionally exposed to untrusted networks as an alternative public webhook endpoint.
- [ ] Gateway and operator credentials are different values.
- [ ] No gateway/operator/exchange secret is embedded in the TradingView URL or alert body.
- [ ] TLS certificate/key paths pass the client-pilot preflight.
- [ ] Selected exchange provider is `Mock` or `BybitTestnet` only.

## B. Payload contract

- [ ] Customer-approved symbols normalize correctly.
- [ ] `buy` / `sell` semantics are documented and approved.
- [ ] Quantity units/precision are documented and approved.
- [ ] The final `eventId` rule is documented.
- [ ] Distinct intended executions produce distinct event IDs.
- [ ] Redelivery of the same intended execution reproduces the same event ID.

## C. Idempotency and conflict behavior

- [ ] First valid event is accepted.
- [ ] Exact redelivery does not create a second local signal/order.
- [ ] Exact redelivery returns the same `SignalId` and `ClientOrderId`.
- [ ] Conflicting reuse of the same event ID returns HTTP 409.
- [ ] Conflict testing does not create a second order.

## D. Audit and operations

- [ ] Every authenticated provider delivery receives a TradeOps delivery ID.
- [ ] Delivery lookup shows the expected outcomes.
- [ ] Correlation can be followed from `eventId -> SignalId -> ClientOrderId`.
- [ ] Reconciliation completes without unexplained issues for the accepted test case.
- [ ] Order lifecycle history is queryable.
- [ ] TradingView delivery metrics are queryable.
- [ ] TradingView health state is queryable after Worker evaluation.
- [ ] Paid-pilot evidence exporter produces both JSON and Markdown.
- [ ] Evidence technical decision is PASS for the agreed acceptance event.
- [ ] Generated report correlation matches the agreed eventId, SignalId and ClientOrderId.

## E. Risk and operator controls

- [ ] Mutating operator actions and the protected paid-pilot evidence reads require the independent operator credential.
- [ ] Customer understands that RiskEngine may reject a signal.
- [ ] Customer understands that health monitoring is observational and does not automatically emergency-stop trading.
- [ ] Emergency-stop/cancellation procedure and authorized operator are documented.

## F. Optional Bybit testnet gate

Complete only if the pilot includes Bybit testnet.

- [ ] Mock phase has already passed.
- [ ] Read-only Bybit testnet smoke succeeds.
- [ ] Credentials are testnet credentials.
- [ ] Test quantity is pre-agreed and intentionally small.
- [ ] Order state is confirmed through lookup/reconciliation rather than relying only on create-order acknowledgement.
- [ ] No mainnet endpoint/credential is used.

## Sign-off record

```text
Client:
Pilot date:
TradeOps version: v1.3.2.1
Exchange stage: Mock / BybitTestnet
Approved TradingView template revision:
Approved eventId rule:
Open issues:
Accepted by client:
Accepted by implementer:
```
