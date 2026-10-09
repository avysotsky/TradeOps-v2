#!/usr/bin/env bash
set -euo pipefail

ENV_FILE="${1:-deploy/client-starter/client-pilot.env}"

fail() {
  echo "CLIENT PILOT PREFLIGHT: FAIL - $*" >&2
  exit 1
}

read_env() {
  local name="$1"
  local line value

  line=$(grep -E "^$name=" "$ENV_FILE" | tail -n 1 || true)
  value="${line#*=}"
  value="${value%$'\r'}"

  if [[ "$value" == \"*\" && "$value" == *\" ]]; then
    value="${value:1:${#value}-2}"
  elif [[ "$value" == \'*\' && "$value" == *\' ]]; then
    value="${value:1:${#value}-2}"
  fi

  printf '%s' "$value"
}

require_nonempty() {
  local name="$1"
  local value
  value=$(read_env "$name")

  [[ -n "$value" ]] || fail "$name is required."
  printf '%s' "$value"
}

require_secret() {
  local name="$1"
  local value
  value=$(require_nonempty "$name")

  [[ "$value" != CHANGE_ME* ]] || fail "$name still contains a CHANGE_ME placeholder."
  [[ ${#value} -ge 32 ]] || fail "$name must be at least 32 characters."
}

[[ -f "$ENV_FILE" ]] || fail "Environment file '$ENV_FILE' does not exist."

command -v docker >/dev/null 2>&1 || fail "docker is required."
docker compose version >/dev/null 2>&1 || fail "Docker Compose v2 is required."

provider=$(require_nonempty "TRADEOPS_EXCHANGE_PROVIDER")
case "$provider" in
  Mock|BybitTestnet) ;;
  *) fail "TRADEOPS_EXCHANGE_PROVIDER must be Mock or BybitTestnet, got '$provider'." ;;
esac

require_secret "TRADEOPS_TRADINGVIEW_GATEWAY_KEY"
require_secret "TRADEOPS_OPERATOR_API_KEY"

gateway_secret=$(read_env "TRADEOPS_TRADINGVIEW_GATEWAY_KEY")
operator_secret=$(read_env "TRADEOPS_OPERATOR_API_KEY")

[[ "$gateway_secret" != "$operator_secret" ]] \
  || fail "TRADEOPS_TRADINGVIEW_GATEWAY_KEY and TRADEOPS_OPERATOR_API_KEY must be different secrets."

tls_cert=$(require_nonempty "TRADEOPS_TRADINGVIEW_TLS_CERT")
tls_key=$(require_nonempty "TRADEOPS_TRADINGVIEW_TLS_KEY")

[[ "$tls_cert" = /* ]] || fail "TRADEOPS_TRADINGVIEW_TLS_CERT must be an absolute path."
[[ "$tls_key" = /* ]] || fail "TRADEOPS_TRADINGVIEW_TLS_KEY must be an absolute path."
[[ -f "$tls_cert" ]] || fail "TLS certificate '$tls_cert' does not exist."
[[ -f "$tls_key" ]] || fail "TLS private key '$tls_key' does not exist."

if [[ "$provider" == "BybitTestnet" ]]; then
  bybit_key=$(require_nonempty "BYBIT_TESTNET_API_KEY")
  bybit_secret=$(require_nonempty "BYBIT_TESTNET_API_SECRET")
  [[ "$bybit_key" != CHANGE_ME* ]] || fail "BYBIT_TESTNET_API_KEY still contains a placeholder."
  [[ "$bybit_secret" != CHANGE_ME* ]] || fail "BYBIT_TESTNET_API_SECRET still contains a placeholder."
fi

telegram_enabled=$(read_env "TELEGRAM_ENABLED")
if [[ "$telegram_enabled" == "true" ]]; then
  require_nonempty "TELEGRAM_BOT_TOKEN" >/dev/null
  require_nonempty "TELEGRAM_CHAT_ID" >/dev/null
fi

docker compose \
  --env-file "$ENV_FILE" \
  -f docker-compose.yml \
  -f deploy/tradingview-gateway/docker-compose.tradingview-gateway.yml \
  -f deploy/client-starter/docker-compose.client-pilot.yml \
  config --quiet

echo "CLIENT PILOT PREFLIGHT: PASS"
echo "Provider: $provider"
echo "TLS certificate: $tls_cert"
echo "TLS private key: $tls_key"
echo "Compose contract: valid"
