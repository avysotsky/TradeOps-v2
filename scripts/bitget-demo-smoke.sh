#!/usr/bin/env bash
set -euo pipefail

: "${BITGET_DEMO_API_KEY:?Set BITGET_DEMO_API_KEY}"
: "${BITGET_DEMO_API_SECRET:?Set BITGET_DEMO_API_SECRET}"
: "${BITGET_DEMO_PASSPHRASE:?Set BITGET_DEMO_PASSPHRASE}"

export TRADEOPS_EXCHANGE_PROVIDER=BitgetDemo
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

echo "Bitget Demo read smoke completed."
echo "All authenticated requests from this provider include paptrading: 1."
