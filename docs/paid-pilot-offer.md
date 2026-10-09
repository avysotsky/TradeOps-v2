# TradeOps Paid Pilot Offer

## Purpose

This pilot is for a client who already has trading rules, TradingView alerts, signals or an existing strategy and needs a reliable execution/integration layer around them.

TradeOps does not provide trading alpha, signal ideas, strategy research or return guarantees.

## Pilot outcome

For a short prospect walkthrough before scoping, use `docs/client-demo-script.md`.

The pilot proves that one agreed client signal flow can move through a controlled execution pipeline with reproducible technical evidence:

```text
TradingView alert
-> trusted HTTPS gateway
-> authenticated TradeOps adapter
-> deterministic event identity
-> risk/execution pipeline
-> persisted local order
-> lifecycle + reconciliation
-> delivery audit / metrics / health
-> evidence package
-> client sign-off
```

## Included

The standard pilot includes:

- review of the client's existing TradingView alert or equivalent signal format;
- agreement on symbol mapping, buy/sell semantics, quantity units and event identity;
- configuration of the TradeOps client starter kit;
- Mock execution as the mandatory first stage;
- authenticated TradingView delivery through the existing gateway path;
- duplicate/redelivery protection;
- conflict detection for reused event IDs;
- persisted `eventId -> SignalId -> ClientOrderId` correlation;
- order lifecycle verification;
- reconciliation;
- TradingView delivery audit, metrics and health state;
- operator-authenticated evidence export;
- JSON and Markdown evidence files;
- documented acceptance result and handoff.

## Optional Bybit Testnet stage

After the Mock acceptance gate passes, the pilot can optionally continue on Bybit Testnet.

The testnet stage uses only pre-agreed test quantities and the existing testnet-restricted exchange adapter.

Bybit mainnet and real-money execution are not part of this offer.

## Client inputs required

Before accepting the pilot, complete `docs/client-intake-questionnaire.md` and classify the request as FIT, FIT WITH SMALL ADAPTATION, SEPARATE SCOPE REQUIRED or NOT A CURRENT FIT.

Before implementation, the client supplies or approves:

- TradingView indicator/strategy/alert source;
- symbols;
- exact meaning of `buy` and `sell`;
- quantity units and precision;
- the stable `eventId` rule;
- expected alert frequency;
- target operating hours;
- public DNS/TLS ownership where a public gateway is required;
- responsible operator contact;
- optional Bybit Testnet credentials if the testnet stage is requested.

No production exchange secret is required for the standard Mock pilot.

## Acceptance criteria

The pilot is technically accepted when the applicable checklist in:

```text
docs/client-pilot-acceptance-criteria.md
```

passes.

The core evidence includes:

- first valid event accepted;
- exact redelivery remains idempotent;
- conflicting event-ID reuse is rejected;
- no duplicate local execution is created;
- correlation resolves to one canonical signal/order identity;
- lifecycle is persisted;
- reconciliation completes without unexplained issues;
- delivery metrics and health are available;
- the paid-pilot evidence exporter returns `PASS`;
- client sign-off is recorded separately from the automated technical result.

## Deliverables

The handoff includes:

- approved signal/TradingView payload contract;
- approved event-ID rule;
- deployment configuration inventory without secret values;
- acceptance checklist;
- `pilot-evidence.json`;
- `pilot-evidence.md`;
- operational stop/rollback instructions;
- open-issues list, if any.

## Explicit exclusions

The pilot does not include:

- strategy design;
- alpha research;
- profitable-signal generation;
- backtest optimization;
- performance or return guarantees;
- Bybit mainnet;
- other real-money venues;
- a second exchange integration;
- HFT or ultra-low-latency guarantees;
- discretionary trade recommendations.

If any excluded capability is required, it should be scoped and priced as a separate project.

## Security boundary

The public TradingView path uses the dedicated gateway boundary.

Sensitive paid-pilot operational reads and operator actions use the independent operator credential when operator authentication is enabled.

Signal-ingress credentials, operator credentials and exchange credentials are separate security boundaries.

Secrets are not part of the generated evidence package.

## Commercial boundary

The paid pilot is an execution/integration engineering engagement.

After qualification, record the agreed engineering boundary in `docs/paid-pilot-scope-template.md`.

Commercial terms, fixed price or hourly billing, hosting responsibility and any post-pilot support period are agreed separately for each client.

The purpose of the pilot is to reduce integration risk and produce verifiable engineering evidence before any broader deployment decision.
