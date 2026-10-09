#!/usr/bin/env bash
set -euo pipefail

: "${KRAKEN_FUTURES_API_KEY:?Set KRAKEN_FUTURES_API_KEY to a read-only Kraken Futures API key}"
: "${KRAKEN_FUTURES_API_SECRET:?Set KRAKEN_FUTURES_API_SECRET to its Base64 API secret}"

export TRADEOPS_EXCHANGE_PROVIDER=KrakenFuturesReadOnly

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

curl --fail --silent --show-error "$BASE_URL/api/account"
echo
curl --fail --silent --show-error "$BASE_URL/api/positions"
echo
curl --fail --silent --show-error "$BASE_URL/api/orders"
echo
echo "Kraken Futures live-host read-only smoke completed. No mutations were executed."
