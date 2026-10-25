#!/usr/bin/env bash
# F2-01 exact-HEAD acceptance, intended for WSL2 Ubuntu with Docker available.
set -Eeuo pipefail
expected="${1:-}"
if [[ ! "$expected" =~ ^[0-9a-f]{40}$ ]]; then
  echo "Usage: bash apps/coincall-terminal/qa/accept-local.sh <40-char-PR-HEAD-SHA> [report-dir]" >&2
  exit 2
fi
# When piped through "git show ... | bash -s", BASH_SOURCE is empty.
# Use the calling checkout. For a file invocation, derive it from the script.
if [[ -n "${BASH_SOURCE[0]:-}" && -f "${BASH_SOURCE[0]}" ]]; then
  repo="$(git -C "$(dirname "${BASH_SOURCE[0]}")" rev-parse --show-toplevel)"
else
  repo="$(git rev-parse --show-toplevel)"
fi
repo="$(realpath "$repo")"
if [[ ! -d "$repo/.git" && ! -f "$repo/.git" ]]; then
  printf 'NOT RUN: missing Git checkout: %s\n' "$repo" >&2
  exit 2
fi
report="${2:-$HOME/TradeOps-acceptance/F2-01/$expected}"
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
build_status=${PIPESTATUS[0]}
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
browser_status=${PIPESTATUS[0]}
set -e
if [[ "$browser_status" -ne 0 ]]; then
  printf 'FAIL: compiled-browser smoke (exit %s)\n' "$browser_status" | tee "$report/status.txt"
  exit 1
fi
echo 'PHASE 3: original VAN static HTML/CSS and compiled React browser parity'
van_sha='702a6f51006be4e9c35c65d21ec7f85c40c5a2d4'
if ! git init -q "$tmp/van"; then
  echo 'NOT RUN: unable to initialize source-pinned VAN reference' | tee "$report/van-parity-status.txt"
  exit 2
fi
git -C "$tmp/van" remote add origin https://github.com/avysotsky/alexvysotsky.com.git
if ! git -C "$tmp/van" fetch --depth=1 origin "$van_sha" > "$report/van-fetch.log" 2>&1; then
  echo 'NOT RUN: VAN exact SHA fetch failed; see van-fetch.log' | tee "$report/van-parity-status.txt"
  exit 2
fi
git -C "$tmp/van" checkout -q --detach FETCH_HEAD
van_actual="$(git -C "$tmp/van" rev-parse HEAD)"
printf 'VAN reference SHA: %s\n' "$van_actual" | tee -a "$report/identity.log"
if [[ "$van_actual" != "$van_sha" ]]; then
  echo 'FAIL: VAN reference SHA mismatch' | tee "$report/van-parity-status.txt"
  exit 1
fi
for f in coincall.html coincall-spreads.html assets/coincall-spreads-full.js; do
  test -s "$tmp/van/frontend/dist/$f" || { echo "FAIL: VAN missing $f" | tee "$report/van-parity-status.txt"; exit 1; }
done
set +e
docker run --rm --user "$(id -u):$(id -g)" -e HOME=/tmp -e npm_config_cache=/tmp/.npm \
  -e EXPECTED_SHA="$expected" -e VAN_SHA="$van_sha" --shm-size=1g \
  -v "$app/qa:/qa:ro" -v "$report:/results" -v "$tmp/van/frontend/dist:/van:ro" \
  mcr.microsoft.com/playwright:v1.56.1-noble \
  bash -euc '
    mkdir -p /tmp/f2-01-parity
    cp /qa/serve-static.mjs /qa/van-browser-parity.mjs /tmp/f2-01-parity/
    cd /tmp/f2-01-parity
    npm install --no-save --no-package-lock --no-audit --no-fund playwright@1.56.1
    node ./serve-static.mjs /results/dist 5181 > /results/van-serve.log 2>&1 &
    pid=$!
    trap "kill $pid >/dev/null 2>&1 || true" EXIT
    node ./van-browser-parity.mjs /results/dist /van /results/van-ui "$EXPECTED_SHA" "$VAN_SHA"
  ' 2>&1 | tee "$report/van-parity.log"
van_status=${PIPESTATUS[0]}
set -e
if [[ "$van_status" -ne 0 ]]; then
  printf 'FAIL: VAN-versus-React visual render gate (exit %s)\n' "$van_status" | tee "$report/van-parity-status.txt" "$report/status.txt"
  exit 1
fi
echo 'PASS: offline screenshot capture of original VAN HTML/CSS and compiled React' | tee "$report/van-parity-status.txt"

test_count="$(grep -E '^# tests [0-9]+' "$report/build-tests.log" | tail -n1 || true)"
printf 'PASS: exact SHA %s | %s | browser 18 checks | offline VAN screenshots captured\n' "$expected" "${test_count:-see TAP log}" | tee "$report/status.txt"
printf 'Evidence: %s\n' "$report"
echo 'Offline original VAN markup/CSS and compiled React screenshots created; independent visual assessment required; VAN JS disabled intentionally.'
