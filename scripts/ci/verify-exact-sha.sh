#!/usr/bin/env bash
# CI-01: non-destructive exact-SHA fallback for Linux / WSL.
# This script runs outside the checked-out target source and never alters main/worker branches.
set -Eeuo pipefail

usage() {
  echo "Usage: bash scripts/ci/verify-exact-sha.sh <62|63|64> <40-char-head-sha> [local-repository-path]" >&2
  exit 2
}

[[ $# -ge 2 && $# -le 3 ]] || usage
pr="$1"
sha="$2"
repo="${3:-.}"
[[ "$sha" =~ ^[0-9a-f]{40}$ ]] || usage
case "$pr" in
  62) gate=backend; head_branch=TradeOps/b1-01-oms-domain-identity-contracts ;;
  63) gate=deribit; head_branch=TradeOps/f1-01-deribit-react-van-parity ;;
  64) gate=coincall; head_branch=TradeOps/f2-01-coincall-react-workstation ;;
  *) usage ;;
esac

command -v git >/dev/null || { echo 'git required' >&2; exit 2; }
root=$(git -C "$repo" rev-parse --show-toplevel)
test "$(git -C "$root" remote get-url origin | grep -Ec '(^|[:/])avysotsky/TradeOps-v2(\.git)?$')" -eq 1 || {
  echo 'Wrong repository: expected avysotsky/TradeOps-v2 origin' >&2
  exit 2
}
scratch=$(mktemp -d)
worktree="$scratch/source"
cleanup() {
  if [[ -d "$worktree" ]]; then
    git -C "$root" worktree remove --force "$worktree" >/dev/null 2>&1 || true
  fi
  rm -rf "$scratch"
}
trap cleanup EXIT

echo "CI-01 gate=$gate PR=$pr requested_sha=$sha"
echo 'Verify requested SHA equals the current remote PR branch HEAD. GitHub authentication may be necessary.'
remote_head=$(git -C "$root" ls-remote origin "refs/heads/$head_branch" | awk '{print $1}')
[[ "$remote_head" == "$sha" ]] || { echo "FAIL: $head_branch current SHA is $remote_head, expected $sha" >&2; exit 1; }
git -C "$root" fetch --no-tags origin "$sha"
git -C "$root" cat-file -e "$sha^{commit}"
git -C "$root" worktree add --detach "$worktree" "$sha"
cd "$worktree"
actual=$(git rev-parse HEAD)
[[ "$actual" == "$sha" ]] || { echo "FAIL: actual SHA $actual" >&2; exit 1; }
export Exchange__Provider=Mock
export DeribitTerminal__ExecutionEnabled=false
export DeribitTerminal__MainnetExecutionEnabled=false
export Telegram__Enabled=false

echo "VERIFIED_SOURCE_SHA=$actual"
case "$gate" in
  backend)
    command -v dotnet >/dev/null || { echo 'FAIL: .NET SDK is not installed' >&2; exit 1; }
    sdk=$(dotnet --version)
    [[ "$sdk" == 10.0.* ]] || { echo "FAIL: .NET 10 SDK required, found $sdk" >&2; exit 1; }
    dotnet --info
    dotnet restore TradeOps.sln
    dotnet build TradeOps.sln --configuration Release --no-restore
    dotnet test TradeOps.sln --configuration Release --no-build --verbosity normal
    echo 'BACKEND_COMPILE_AND_TEST=PASS'
    echo 'POSTGRES_MOCK_SMOKE=NOT_RUN (requires PG and CI build.yml smoke sequence)'
    ;;
  deribit|coincall)
    command -v node >/dev/null || { echo 'FAIL: Node 24 required' >&2; exit 1; }
    command -v npm >/dev/null || { echo 'FAIL: npm required' >&2; exit 1; }
    [[ "$(node --version)" == v24.* ]] || { echo "FAIL: Node 24 required; found $(node --version)" >&2; exit 1; }
    echo "Node $(node --version), npm $(npm --version)"
    if [[ "$gate" == deribit ]]; then
      folder=apps/trade-terminal
      test -f "$folder/tests/vanLayout.test.mjs" || { echo 'FAIL: missing VAN layout test' >&2; exit 1; }
    else
      folder=apps/coincall-terminal
    fi
    test -f "$folder/package.json" || { echo "FAIL: missing required app $folder" >&2; exit 1; }
    cd "$folder"
    if [[ -f package-lock.json ]]; then
      echo 'LOCKFILE=present; npm ci'
      npm ci --no-audit --no-fund
    else
      echo 'LOCKFILE=absent; npm install --no-package-lock (NOT reproducibly pinned)'
      npm install --no-package-lock --no-audit --no-fund
    fi
    if [[ "$gate" == deribit ]]; then
      npm test
      node --test tests/vanLayout.test.mjs
      npm run build
    else
      npm run check
    fi
    echo "FRONTEND_GATE=$gate PASS at $actual (lockfile status above)"
    ;;
esac
echo 'CI-01 local fallback completed; this is NOT a GitHub Actions check run.'
