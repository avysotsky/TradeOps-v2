#!/usr/bin/env bash
set -euo pipefail

API_URL="${API_URL:-http://localhost:8080}"
SIGNAL_ID="d0f8625f-2ad4-44eb-a1ec-22acbfbb2e58"

printf 'Waiting for TradeOps API at %s...\n' "$API_URL"
for attempt in $(seq 1 30); do
  if curl --fail --silent "$API_URL/health" >/dev/null; then
    printf 'API is ready.\n\n'
    break
  fi

  if [ "$attempt" -eq 30 ]; then
    printf 'API did not become ready.\n' >&2
    exit 1
  fi

  sleep 2
done

printf '1) Submit deterministic signal (expected: PartiallyFilled, 60%%)\n'
curl --fail --silent --show-error \
  -H 'Content-Type: application/json' \
  -d "{\"symbol\":\"BTCUSDT\",\"side\":\"Buy\",\"quantity\":0.001,\"source\":\"docker-demo\",\"signalId\":\"$SIGNAL_ID\"}" \
  "$API_URL/api/signals"
printf '\n\n'

printf '2) Reconcile local state with mock exchange (expected: Filled)\n'
curl --fail --silent --show-error \
  -X POST \
  "$API_URL/api/system/reconcile"
printf '\n\n'

printf '3) Retry the exact same logical signal (expected: existing Filled order, no duplicate)\n'
curl --fail --silent --show-error \
  -H 'Content-Type: application/json' \
  -d "{\"symbol\":\"BTCUSDT\",\"side\":\"Buy\",\"quantity\":0.001,\"source\":\"docker-demo-retry\",\"signalId\":\"$SIGNAL_ID\"}" \
  "$API_URL/api/signals"
printf '\n\nDemo completed.\n'
