# Client pilot runbook

TradeOps v1.3.2.1 defines a controlled path for the first paid TradingView integration pilot and its operator-authenticated evidence handoff.

## Scope

The pilot validates execution automation for customer-supplied trading rules/signals.

In scope:

- TradingView alert normalization;
- stable event identity;
- duplicate/redelivery handling;
- Mock execution first;
- optional Bybit testnet execution after Mock sign-off;
- order lifecycle persistence;
- reconciliation;
- delivery audit/metrics/health;
- operator controls and operational alerts.

Out of scope:

- strategy design;
- alpha research;
- profitability guarantees;
- Bybit mainnet or other real-money venues;
- HFT/low-latency guarantees.

## Phase 0 — customer intake

Complete `docs/client-intake-questionnaire.md` first. Do not start implementation until the request is classified as FIT or FIT WITH SMALL ADAPTATION.

Record and agree these items before configuration:

| Item | Required decision |
| --- | --- |
| TradingView source | indicator / strategy / existing alert logic |
| Symbols | exact symbols expected by TradeOps/exchange |
| Action mapping | what customer `buy` and `sell` mean |
| Quantity semantics | contracts/base quantity and expected decimal precision |
| Event identity | exact stable `eventId` construction |
| Optional risk fields | riskPercent / stopLoss / takeProfit ownership |
| Alert frequency | typical and peak delivery rate |
| Operating hours | whether no-success health monitoring is meaningful |
| Target stage | Mock first; optional BybitTestnet after acceptance |
| Operator contacts | person allowed to trigger mutating operator actions |
| Alert channel | Telegram enabled/disabled and responsible recipient |
| Public endpoint | DNS name and TLS certificate ownership |

Any ambiguity in symbol, action, quantity or event identity must be resolved before the pilot.

## Phase 1 — configuration preflight

1. Create the private pilot env file from `deploy/client-starter/client-pilot.env.example`.
2. Keep `TRADEOPS_EXCHANGE_PROVIDER=Mock`.
3. Configure distinct gateway/operator secrets.
4. Configure valid TLS certificate/key paths.
5. Run:

```bash
bash scripts/client-pilot-preflight.sh deploy/client-starter/client-pilot.env
```

Acceptance gate: `CLIENT PILOT PREFLIGHT: PASS`.

## Phase 2 — local customer demo

Run:

```bash
bash scripts/tradingview-customer-demo.sh
```

Acceptance gate: `CUSTOMER TRADINGVIEW DEMO: PASS`.

This confirms the repository and host can demonstrate:

```text
Accepted -> Redelivered -> Conflict
eventId -> SignalId -> ClientOrderId
reconciliation -> Filled
metrics -> health
```

## Phase 3 — public Mock webhook pilot

Start the pilot stack with the production-like Nginx gateway.

Configure one TradingView alert using the approved message template and public HTTPS gateway URL.

For each agreed test event, capture:

- TradingView event ID;
- TradeOps delivery ID;
- SignalId;
- ClientOrderId;
- adapter HTTP result;
- final local order state;
- delivery outcome;
- health state.

Required tests:

1. a valid new event;
2. exact redelivery of the same event;
3. a deliberately conflicting reuse of a test event ID;
4. reconciliation after an accepted event;
5. health/metrics inspection.

Acceptance gate: all applicable criteria in `docs/client-pilot-acceptance-criteria.md` pass in Mock.

## Phase 4 — optional Bybit testnet

Only after explicit Mock sign-off:

1. change `TRADEOPS_EXCHANGE_PROVIDER=BybitTestnet`;
2. add Bybit testnet API key/secret;
3. rerun preflight;
4. run the existing read-only smoke check first;
5. submit only the pre-agreed small testnet quantity;
6. inspect lifecycle and reconciliation.

TradeOps code restricts the Bybit adapter to the official testnet host. Mainnet remains out of scope.

## Phase 5 — evidence package

For each agreed acceptance event, generate evidence:

```bash
export TRADEOPS_PILOT_EVENT_ID='THE_APPROVED_EVENT_ID'
export TRADEOPS_PILOT_CLIENT='Client name'
export TRADEOPS_PILOT_STAGE='Mock'
export TRADEOPS_OPERATOR_API_KEY='THE_CONFIGURED_OPERATOR_KEY'

bash scripts/generate-pilot-evidence.sh
```

Acceptance gate: `PILOT EVIDENCE: PASS`.

The evidence exporter authenticates the sensitive signal/order/reconciliation and TradingView operations reads with the operator credential. Review the generated Markdown with the client. Automated technical PASS does not replace client acceptance.

## Phase 6 — handoff

Deliver:

- generated `pilot-evidence.json` and `pilot-evidence.md`;
- approved TradingView alert template;
- approved event-ID rule;
- deployment env-variable inventory without secret values;
- health thresholds;
- operator contacts;
- acceptance record;
- rollback/stop instructions.

Stop/rollback actions are operational actions, not automated health responses. Health monitoring does not disable trading or cancel orders by itself.
