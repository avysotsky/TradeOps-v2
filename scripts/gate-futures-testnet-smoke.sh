#!/usr/bin/env bash
set -euo pipefail

: "${GATE_TESTNET_API_KEY:?Set GATE_TESTNET_API_KEY}"
: "${GATE_TESTNET_API_SECRET:?Set GATE_TESTNET_API_SECRET}"

export TRADEOPS_EXCHANGE_PROVIDER=GateFuturesTestnet

BASE_URL="${TRADEOPS_BASE_URL:-http://localhost:8080}"

docker compose up --build -d postgres api

for attempt in $(seq 1 30); do
  if curl --fail --silent "$BASE_URL/health" >/dev/null; then
    break
  fi

  if [ "$attempt" -eq 30 ]; then
    docker compose logs api >&2 || true
    exit 1
  fi

  sleep 2
done

echo "Account:"
curl --fail --silent --show-error "$BASE_URL/api/account"
echo

echo "Positions:"
curl --fail --silent --show-error "$BASE_URL/api/positions"
echo

echo "Open orders:"
curl --fail --silent --show-error "$BASE_URL/api/orders"
echo

echo "Gate Futures TestNet smoke completed."
echo "Order placement is available through the normal TradeOps signal/order flow."
