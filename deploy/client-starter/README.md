# Client Integration Starter Kit

TradeOps v1.3.2.2 packages the files needed to move from the customer demo into a controlled first paid TradingView pilot and operator-authenticated evidence handoff.

This kit is deployment/onboarding material. It does not change the execution engine and it does not enable mainnet trading.

## Included files

```text
deploy/client-starter/
├── client-pilot.env.example
├── docker-compose.client-pilot.yml
├── tradingview-alert-template.json
├── tradingview-alert-sample.json
└── README.md
```

Related operational documents:

```text
docs/client-pilot-runbook.md
docs/client-pilot-acceptance-criteria.md
```

## 1. Prepare a private environment file

Copy the example outside the repository or to an ignored local file:

```bash
cp deploy/client-starter/client-pilot.env.example \
   deploy/client-starter/client-pilot.env
```

Fill in:

- execution provider: `Mock` or `BybitTestnet`;
- a random internal TradingView gateway secret;
- a different random operator API secret;
- absolute TLS certificate/key paths;
- Bybit testnet credentials only when `BybitTestnet` is selected;
- optional health/Telegram settings.

Never send these secrets inside a TradingView alert message.

## 2. Run preflight

```bash
bash scripts/client-pilot-preflight.sh \
  deploy/client-starter/client-pilot.env
```

Expected result:

```text
CLIENT PILOT PREFLIGHT: PASS
```

The preflight checks required secrets, provider choice, TLS files, conditional Bybit testnet credentials and the merged Docker Compose configuration.

## 3. Start the pilot stack

```bash
docker compose \
  --env-file deploy/client-starter/client-pilot.env \
  -f docker-compose.yml \
  -f deploy/tradingview-gateway/docker-compose.tradingview-gateway.yml \
  -f deploy/client-starter/docker-compose.client-pilot.yml \
  up --build -d
```

This enables:

- the production-like TradingView HTTPS gateway;
- the TradingView adapter;
- independently authenticated operator actions and protected paid-pilot evidence reads;
- TradingView delivery health evaluation.

The base API port must still be restricted from untrusted networks by the host firewall/security group. Public TradingView traffic should enter through HTTPS port 443 on the gateway.

## 4. Configure TradingView

Use `tradingview-alert-template.json` as the alert-message starting point.

The template intentionally contains TradingView placeholders, so it is not a standalone JSON document until TradingView renders the placeholders.

`tradingview-alert-sample.json` is a rendered valid-JSON example for review/testing.

The client must approve the final `eventId` construction. Different intended executions require different event IDs; provider redelivery of one logical event must reuse the same event ID.

## 5. Execute the pilot runbook

Follow:

```text
docs/client-pilot-runbook.md
```

Do not move from Mock to BybitTestnet until the Mock acceptance gate has passed.

## 6. Generate pilot evidence

For the agreed test `eventId`:

```bash
export TRADEOPS_PILOT_EVENT_ID='THE_APPROVED_EVENT_ID'
export TRADEOPS_PILOT_CLIENT='Client name'
export TRADEOPS_PILOT_STAGE='Mock'
export TRADEOPS_OPERATOR_API_KEY='THE_CONFIGURED_OPERATOR_KEY'

bash scripts/generate-pilot-evidence.sh
```

Expected technical result:

```text
PILOT EVIDENCE: PASS
```

The generated JSON/Markdown package is read-only evidence. Sensitive evidence reads use the operator credential when operator authentication is enabled. It does not replace customer sign-off.

## 7. Sign off

Use:

```text
docs/client-pilot-acceptance-criteria.md
```

The pilot is an engineering acceptance exercise. Profitability, alpha, signal quality and trading returns are explicitly outside the acceptance criteria.
