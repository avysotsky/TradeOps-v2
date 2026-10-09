# TradeOps Paid Pilot Scope Template

Use this document after the client has passed `docs/client-intake-questionnaire.md` and before implementation starts.

This template records the agreed engineering scope of one paid pilot. Commercial price/payment terms and legal terms are agreed separately.

## 1. Client

**Client / company:**

**Primary technical contact:**

**Authorized operator:**

**Pilot stage:**

- [ ] Mock only
- [ ] Mock + optional Bybit Testnet after Mock acceptance

## 2. Client's existing trading decision source

**Source:**

- [ ] TradingView indicator
- [ ] TradingView strategy
- [ ] Existing alert logic
- [ ] Existing bot/service
- [ ] Other

**Short description of the existing client logic:**

The client remains responsible for the trading decision/strategy. TradeOps is responsible for the agreed execution/integration engineering scope below.

## 3. Agreed signal contract

**Symbols:**

**buy means:**

**sell means:**

**Quantity semantics:**

**Required precision:**

**Optional fields used:**

- [ ] riskPercent
- [ ] stopLoss
- [ ] takeProfit
- [ ] none

**Approved eventId rule:**

```text
Same intended execution -> same eventId
Different intended execution -> different eventId
```

## 4. Agreed integration path

Standard path:

```text
client signal / TradingView
-> HTTPS gateway
-> TradeOps adapter
-> deterministic event identity
-> existing risk/execution pipeline
-> persisted order/lifecycle
-> reconciliation
-> audit/metrics/health
-> evidence package
```

**Public hostname / endpoint responsibility:**

**DNS owner:**

**TLS owner:**

**Hosting responsibility:**

## 5. Included work

Unless explicitly changed below, the pilot includes:

- configure the existing TradeOps client starter kit;
- map the approved client signal fields into the current TradingView/pilot contract;
- configure gateway and operator security boundaries;
- execute the mandatory Mock acceptance flow;
- verify first delivery, exact redelivery and conflicting event-ID reuse;
- verify one canonical `eventId -> SignalId -> ClientOrderId` chain;
- verify persisted local lifecycle and reconciliation;
- inspect delivery audit, metrics and health;
- generate the operator-authenticated evidence package;
- review the acceptance checklist;
- produce the agreed handoff package.

## 6. Client-specific small adaptations

List only bounded changes accepted as part of this pilot.

**Adaptation 1:**

**Adaptation 2:**

**Adaptation 3:**

If a requested change introduces a new exchange, new strategy/risk algorithm, mainnet execution, materially new order semantics or a new product subsystem, move it to separate scope rather than adding it here.

## 7. Optional Bybit Testnet stage

Complete only if requested.

- [ ] Mock acceptance must pass first.
- [ ] Client supplies dedicated Bybit Testnet credentials through the agreed secure configuration channel.
- [ ] Read-only testnet smoke runs before order submission.
- [ ] Test quantities are agreed in advance.
- [ ] Exchange state is verified by lookup/reconciliation rather than create-order acknowledgement alone.

**Approved test symbols:**

**Approved maximum test quantity:**

Mainnet/real-money execution remains excluded.

## 8. Technical acceptance

The technical acceptance source of truth is:

```text
docs/client-pilot-acceptance-criteria.md
```

The pilot must produce:

- [ ] accepted first event;
- [ ] idempotent exact redelivery;
- [ ] rejected conflicting event-ID reuse;
- [ ] canonical signal/order correlation;
- [ ] lifecycle evidence;
- [ ] reconciliation evidence;
- [ ] delivery metrics;
- [ ] health state;
- [ ] `pilot-evidence.json`;
- [ ] `pilot-evidence.md`;
- [ ] technical evidence decision PASS for the agreed event;
- [ ] separate client sign-off record.

## 9. Deliverables

Standard handoff:

- approved payload/signal contract;
- approved eventId rule;
- deployment/configuration inventory without secret values;
- acceptance checklist;
- pilot evidence JSON;
- pilot evidence Markdown;
- open-issues list;
- operational stop/rollback instructions.

**Additional agreed deliverables:**

## 10. Client responsibilities

The client is responsible for:

- supplying an existing trading decision source/rules;
- approving symbol, action and quantity semantics;
- approving the eventId rule;
- providing DNS/TLS access where applicable;
- providing testnet credentials only if the optional Bybit Testnet stage is requested;
- identifying the authorized operator;
- reviewing and signing off the agreed technical evidence.

## 11. Explicit exclusions

Unless separately scoped, the pilot excludes:

- strategy design;
- alpha research;
- profitable signal generation;
- backtest optimization;
- profit/return guarantees;
- Bybit mainnet;
- any other real-money exchange deployment;
- second-exchange integration;
- HFT / ultra-low-latency engineering;
- discretionary trading recommendations;
- unrelated dashboard, billing, multitenancy or Kubernetes work.

## 12. Change control

A request is **configuration** if the current product already supports it and only values/mappings must change.

A request is a **small adaptation** if it is bounded, does not change the product boundary and is explicitly added to Section 6.

A request becomes **separate scope** if it introduces a new capability, venue, execution model, security boundary or strategy/risk decision logic.

Do not silently absorb separate-scope work into the pilot.

## 13. Commercial fields

These fields are completed outside the public product specification.

**Commercial model:**

- [ ] fixed-price pilot
- [ ] hourly
- [ ] other

**Agreed price/rate:**

**Payment/milestone terms:**

**Support period after handoff:**

**Hosting cost responsibility:**

## 14. Approval

**Client scope approved by:**

**Implementer scope approved by:**

**Approval date:**

**Open assumptions / dependencies:**
