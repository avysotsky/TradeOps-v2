#!/usr/bin/env bash
set -euo pipefail

: "${OKX_DEMO_API_KEY:?Set OKX_DEMO_API_KEY}"
: "${OKX_DEMO_API_SECRET:?Set OKX_DEMO_API_SECRET}"
: "${OKX_DEMO_PASSPHRASE:?Set OKX_DEMO_PASSPHRASE}"

export TRADEOPS_EXCHANGE_PROVIDER=OkxDemo
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

echo "OKX Demo read smoke completed."
echo "All OKX requests made by this provider include x-simulated-trading: 1."
