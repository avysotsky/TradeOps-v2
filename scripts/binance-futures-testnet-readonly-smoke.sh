#!/usr/bin/env bash
set -euo pipefail

: "${BINANCE_FUTURES_TESTNET_API_KEY:?Set BINANCE_FUTURES_TESTNET_API_KEY to a Binance Futures testnet API key}"
: "${BINANCE_FUTURES_TESTNET_API_SECRET:?Set BINANCE_FUTURES_TESTNET_API_SECRET to the matching Binance Futures testnet API secret}"

export TRADEOPS_EXCHANGE_PROVIDER=BinanceFuturesTestnet

BASE_URL="${TRADEOPS_BASE_URL:-http://localhost:8080}"

echo "Starting TradeOps API in BinanceFuturesTestnet mode..."
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
echo "Binance USD-M Futures testnet read-only smoke test completed successfully."
