#!/usr/bin/env bash
set -euo pipefail

: "${HYPERLIQUID_TESTNET_USER_ADDRESS:?Set HYPERLIQUID_TESTNET_USER_ADDRESS to the actual Hyperliquid testnet account address}"

export TRADEOPS_EXCHANGE_PROVIDER=HyperliquidTestnet

BASE_URL="${TRADEOPS_BASE_URL:-http://localhost:8080}"

echo "Starting TradeOps API in HyperliquidTestnet read-only mode..."
docker compose up --build -d postgres api

for attempt in $(seq 1 30); do
  if curl --fail --silent "$BASE_URL/health" >/dev/null; then
    break
  fi

  if [ "$attempt" -eq 30 ]; then
    echo "TradeOps API did not become healthy." >&2
    docker compose logs api >&2 || true
    exit 1
  fi

  sleep 2
done

echo
echo "Account:"
curl --fail --silent --show-error "$BASE_URL/api/account"
echo

echo
echo "Positions:"
curl --fail --silent --show-error "$BASE_URL/api/positions"
echo

echo
echo "Open orders:"
curl --fail --silent --show-error "$BASE_URL/api/orders"
echo

echo
echo "Hyperliquid testnet read-only smoke test completed successfully."
