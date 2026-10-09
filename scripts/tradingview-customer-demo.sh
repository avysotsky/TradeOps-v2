#!/usr/bin/env bash
set -euo pipefail

export TRADEOPS_TRADINGVIEW_DEMO_GATEWAY_KEY="${TRADEOPS_TRADINGVIEW_DEMO_GATEWAY_KEY:-local-customer-demo-gateway-key-at-least-32-characters}"
export TRADEOPS_TRADINGVIEW_DEMO_OPERATOR_API_KEY="${TRADEOPS_TRADINGVIEW_DEMO_OPERATOR_API_KEY:-local-customer-demo-operator-key-at-least-32-characters}"
export TRADEOPS_TRADINGVIEW_DEMO_API_URL="${TRADEOPS_TRADINGVIEW_DEMO_API_URL:-http://localhost:8080}"

docker compose \
  -f docker-compose.yml \
  -f deploy/customer-demo/docker-compose.tradingview-demo.yml \
  up --build -d

for attempt in $(seq 1 40); do
  if curl --fail --silent "${TRADEOPS_TRADINGVIEW_DEMO_API_URL}/health/ready" >/dev/null; then
    break
  fi

  if [ "$attempt" -eq 40 ]; then
    echo "TradeOps API did not become ready." >&2
    exit 1
  fi

  sleep 2
done

dotnet run --project tools/TradeOps.TradingViewDemo

echo
echo "Demo stack remains running for inspection."
echo "Stop it with:"
echo "docker compose -f docker-compose.yml -f deploy/customer-demo/docker-compose.tradingview-demo.yml down"
