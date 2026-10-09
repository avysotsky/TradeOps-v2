#!/usr/bin/env bash
set -euo pipefail

if [ "$#" -ge 1 ]; then
  export TRADEOPS_PILOT_EVENT_ID="$1"
fi

if [ "$#" -ge 2 ]; then
  export TRADEOPS_PILOT_OUTPUT_DIR="$2"
fi

: "${TRADEOPS_PILOT_EVENT_ID:?TRADEOPS_PILOT_EVENT_ID or first argument is required}"

export TRADEOPS_PILOT_API_URL="${TRADEOPS_PILOT_API_URL:-http://localhost:8080}"
export TRADEOPS_PILOT_OUTPUT_DIR="${TRADEOPS_PILOT_OUTPUT_DIR:-pilot-evidence}"

dotnet run --project tools/TradeOps.PilotEvidence
