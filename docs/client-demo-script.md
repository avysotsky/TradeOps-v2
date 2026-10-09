# TradeOps Client Demo Script

Use this script for a short live demonstration to a qualified prospect before or during paid-pilot scoping.

Target duration: 10–15 minutes.

The goal is not to teach the full codebase. The goal is to prove that TradeOps solves execution/integration problems around an existing client strategy or signal source.

## Demo objective

By the end of the demo, the client should understand that TradeOps can:

- accept an external trading event;
- preserve one stable logical execution identity;
- prevent duplicate execution on redelivery;
- reject conflicting reuse of the same event identity;
- persist signal/order correlation;
- reconcile execution state;
- expose operational evidence;
- produce a client-readable evidence package.

The demo does **not** claim profitable trading logic, alpha or mainnet readiness.

## 0. Opening — 60 seconds

Say, in substance:

> You already decide when to trade. TradeOps is the execution/integration layer around that decision. This demo shows how one signal is authenticated, deduplicated, executed in Mock mode, reconciled and turned into verifiable evidence.

Set expectations:

- this is Mock execution;
- no real money is involved;
- the focus is reliability, idempotency, auditability and operational control;
- the same pilot path can optionally continue to Bybit Testnet after Mock acceptance.

## 1. Show the architecture — 1 minute

Show this flow:

```text
TradingView / client signal
-> trusted gateway
-> TradeOps adapter
-> canonical signal
-> deterministic ClientOrderId
-> execution
-> PostgreSQL audit
-> reconciliation
-> metrics / health
-> evidence package
```

Explain only three concepts:

1. **Stable event identity** — one intended trade maps to one logical execution.
2. **Idempotency** — provider redelivery does not create a duplicate order.
3. **Evidence** — the result can be traced from provider event to signal to order lifecycle.

Avoid deep class/interface discussion unless the client asks.

## 2. Run the customer demo — 3–4 minutes

From the repository root:

```bash
bash scripts/tradingview-customer-demo.sh
```

Call attention to the sequence:

```text
Accepted
-> Redelivered
-> Conflict
-> Reconcile
-> Filled
-> Metrics
-> Health
```

Do not narrate every log line.

Highlight these visible proofs:

- first event is accepted;
- exact redelivery returns the same signal/order identity;
- conflicting reuse of the event ID returns HTTP 409;
- reconciliation completes;
- lifecycle reaches the expected final state;
- metrics and health are queryable.

Expected final marker:

```text
CUSTOMER TRADINGVIEW DEMO: PASS
```

## 3. Explain duplicate protection — 2 minutes

Use the event identity already shown by the demo.

Explain:

```text
same eventId
-> same canonical SignalId
-> same ClientOrderId
-> no second local execution
```

Then explain the conflict case:

```text
same eventId + different execution fields
-> reject
-> HTTP 409
-> no second order
```

Client value:

- webhook retries are expected in distributed systems;
- duplicate delivery should not silently become duplicate execution;
- conflicting reuse should be visible instead of guessed around.

Do not describe idempotency as a guarantee against every possible exchange-side failure. State that exchange ambiguity is handled through lookup/reconciliation rather than blind retry.

## 4. Show correlation and lifecycle evidence — 2 minutes

Show or describe the correlation:

```text
eventId
-> SignalId
-> ClientOrderId
-> order lifecycle
-> reconciliation result
```

Point out that this is useful when the client asks:

- “Did my alert arrive?”
- “Was this a retry?”
- “Which order belongs to this alert?”
- “What state did the order reach?”
- “Was reconciliation clean?”

This is the core operational value of the system.

## 5. Show the evidence package — 2 minutes

For an agreed event, the pilot path generates:

```text
pilot-evidence.json
pilot-evidence.md
```

The evidence package records the technical acceptance chain and produces a PASS/FAIL engineering decision.

Explain the boundary clearly:

- PASS means the integration behaved according to the agreed engineering checks;
- PASS does not mean the strategy is profitable;
- client sign-off remains separate from automated technical acceptance.

Sensitive evidence reads require the independent operator credential when operator authentication is enabled.

## 6. Show the security boundaries — 1 minute

Keep this high-level.

Explain that TradeOps separates:

- public TradingView gateway identity;
- internal gateway credential;
- operator credential;
- optional signed signal-ingress credential/HMAC;
- exchange credentials.

Do not display secret values.

Emphasize that the standard pilot does not require production exchange credentials for Mock mode.

## 7. Optional Bybit Testnet — 1 minute

Only discuss this if relevant to the prospect.

State:

- Mock must pass first;
- Bybit Testnet is optional;
- testnet credentials are supplied separately;
- small pre-agreed quantities are used;
- create-order acknowledgement is not treated as proof of final execution state;
- lookup/reconciliation confirms state.

Do not imply mainnet support in the current standard offer.

## 8. Close with qualification — 1–2 minutes

Ask only the questions that determine fit:

1. Do you already have the trading rule/signal logic?
2. Can it emit TradingView alerts or HTTPS webhooks?
3. Which symbols and buy/sell semantics are required?
4. What does quantity mean in your current system?
5. Can one intended execution be assigned a stable event ID?
6. Is Mock enough for the first acceptance stage?
7. Do you want an optional Bybit Testnet stage after Mock passes?

If the answers fit the current product boundary, proceed to:

```text
docs/client-intake-questionnaire.md
-> docs/paid-pilot-scope-template.md
```

## Objection handling

### “Can you provide the profitable strategy?”

Answer:

> No. TradeOps assumes you already have the trading decision logic. The product is the execution, integration, reliability and audit layer around that logic.

### “Can we go straight to real money?”

Answer:

> Not in the current standard pilot. The acceptance path is Mock first, then optional Bybit Testnet. Mainnet is outside the current scope.

### “What if TradingView sends the alert twice?”

Answer:

> Exact redelivery is mapped to the same logical execution identity, so the system returns the same signal/order correlation rather than intentionally creating a second local execution.

### “How do I know what happened to a signal?”

Answer:

> The delivery audit and persisted correlation let us trace the event to its canonical signal, ClientOrderId, lifecycle and reconciliation result, and the pilot exporter packages that evidence.

### “Can you support another exchange?”

Answer:

> The current public product supports Mock and a Bybit Testnet adapter. A second exchange is separate scope.

## Demo failure rule

If any expected demo step fails:

- do not improvise around it;
- record the failing step;
- capture the relevant logs/evidence;
- classify it as a reproducible delivery defect;
- fix and rerun before presenting the result as accepted.

Do not explain away a failed acceptance check as “probably fine.”

## Demo success exit

A successful prospect demo ends with one of three next actions:

- **FIT** -> complete intake and prepare the paid-pilot scope;
- **FIT WITH SMALL ADAPTATION** -> document the bounded adaptation before quoting;
- **SEPARATE SCOPE / NOT A CURRENT FIT** -> do not distort the current product to win the deal.
