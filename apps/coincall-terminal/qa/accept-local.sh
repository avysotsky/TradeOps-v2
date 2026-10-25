#!/usr/bin/env bash
# F2-01 exact-HEAD acceptance, intended for WSL2 Ubuntu with Docker available.
set -Eeuo pipefail
expected="\${1:-}"
if [[ ! "$expected" =~ ^[0-9a-f]{40}$ ]]; then
  echo "Usage: bash apps/coincall-terminal/qa/accept-local.sh <40-char-PR-HEAD-SHA> [report-dir]" >&2
  exit 2
fi
repo="$(git -C "$(dirname "\${BASH_SOURCE[0]}")" rev-parse --show-toplevel)"
report="\${2:-$HOME/TradeOps-acceptance/F2-01/$expected}"
mkdir -p "$report"
printf 'Expected SHA: %s\nUTC start: %s\n' "$expected" "$(date -u +%FT%TZ)" | tee "$report/identity.log"
if ! command -v docker >/dev/null || ! docker info >/dev/null 2>&1; then
  echo 'NOT RUN: WSL2 Docker not available' | tee "$report/status.txt"
  exit 2
fi
if ! git -C "$repo" fetch --no-tags origin refs/heads/TradeOps/f2-01-coincall-react-workstation; then
  echo 'NOT RUN: unable to fetch F2-01 PR branch' | tee "$report/status.txt"
  exit 2
fi
actual="$(git -C "$repo" rev-parse FETCH_HEAD)"
printf 'Fetched HEAD: %s\n' "$actual" | tee -a "$report/identity.log"
if [[ "$actual" != "$expected" ]]; then
  echo 'FAIL: PR HEAD changed; use the new exact SHA' | tee "$report/status.txt"
  exit 1
fi
tmp="$(mktemp -d)"
cleanup() {
  git -C "$repo" worktree remove --force "$tmp/checkout" >/dev/null 2>&1 || true
  rm -rf "$tmp"
}
trap cleanup EXIT
git -C "$repo" worktree add --detach "$tmp/checkout" "$expected" > "$report/worktree.log" 2>&1
[[ "$(git -C "$tmp/checkout" rev-parse HEAD)" == "$expected" ]] || {
  echo 'FAIL: checkout HEAD mismatch' | tee "$report/status.txt"
  exit 1
}
app="$tmp/checkout/apps/coincall-terminal"
echo 'PHASE 1: Node 24, unit tests, strict TypeScript and Vite build'
set +e
docker run --rm --user "$(id -u):$(id -g)" -e HOME=/tmp -e npm_config_cache=/tmp/.npm \
  -v "$app:/source:ro" -v "$report:/results" node:24-bookworm \
  bash -euc '
    mkdir -p /tmp/f2-01 && cp -a /source/. /tmp/f2-01/
    cd /tmp/f2-01
    echo NODE_VERSION=$(node --version)
    echo NPM_VERSION=$(npm --version)
    npm install --no-package-lock --no-audit --no-fund
    npm ls --depth=0
    npm test
    npm run build
    cp -a dist /results/dist
  ' 2>&1 | tee "$report/build-tests.log"
build_status=\${PIPESTATUS[0]}
set -e
if [[ "$build_status" -ne 0 ]]; then
  printf 'FAIL: Node24 test/build (exit %s)\n' "$build_status" | tee "$report/status.txt"
  exit 1
fi
echo 'PHASE 2: built React in Chromium, 1440x900 and 768x900'
mkdir -p "$report/ui"
set +e
docker run --rm --user "$(id -u):$(id -g)" -e HOME=/tmp -e npm_config_cache=/tmp/.npm \
  -e EXPECTED_SHA="$expected" --shm-size=1g \
  -v "$app/qa:/qa:ro" -v "$report:/results" \
  mcr.microsoft.com/playwright:v1.56.1-noble \
  bash -euc '
    mkdir -p /tmp/f2-01-browser
    cp /qa/browser-check.mjs /qa/serve-static.mjs /tmp/f2-01-browser/
    cd /tmp/f2-01-browser
    echo BROWSER_NODE_VERSION=$(node --version)
    npm install --no-save --no-package-lock --no-audit --no-fund playwright@1.56.1
    node ./serve-static.mjs /results/dist 5181 > /results/serve.log 2>&1 &
    server_pid=$!
    trap "kill $server_pid >/dev/null 2>&1 || true" EXIT
    node ./browser-check.mjs /results/ui "$EXPECTED_SHA"
  ' 2>&1 | tee "$report/browser.log"
browser_status=\${PIPESTATUS[0]}
set -e
if [[ "$browser_status" -ne 0 ]]; then
  printf 'FAIL: compiled-browser smoke (exit %s)\n' "$browser_status" | tee "$report/status.txt"
  exit 1
fi
test_count="$(grep -E '^# tests [0-9]+' "$report/build-tests.log" | tail -n1 || true)"
printf 'PASS: exact SHA %s | %s | browser 14 checks\n' "$expected" "\${test_count:-see TAP log}" | tee "$report/status.txt"
printf 'Evidence: %s\n' "$report"
echo 'Screenshots require a separate visual comparison with the authenticated VAN reference.'
