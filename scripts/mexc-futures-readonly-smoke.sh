#!/usr/bin/env bash
set -euo pipefail

: "${MEXC_FUTURES_API_KEY:?Set MEXC_FUTURES_API_KEY to a read-only MEXC Futures API key}"
: "${MEXC_FUTURES_API_SECRET:?Set MEXC_FUTURES_API_SECRET to the matching MEXC Futures API secret}"

export TRADEOPS_EXCHANGE_PROVIDER=MexcFuturesReadOnly

BASE_URL="${TRADEOPS_BASE_URL:-http://localhost:8080}"

echo "Starting TradeOps API in MexcFuturesReadOnly mode..."
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
echo "MEXC Futures read-only smoke test completed successfully."
echo "No order placement or cancellation was performed."
