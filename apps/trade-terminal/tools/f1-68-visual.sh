#!/usr/bin/env bash
# F1-68 owner-side screenshot runner; CI-01 remains the only test/build gate.
set -Eeuo pipefail
repo="$1"; sha="$2"; van_src="$3"
[[ "$sha" =~ ^[a-f0-9]{40}$ ]] || { echo "INVALID_SHA"; exit 2; }
branch='TradeOps/f1-01-deribit-react-van-parity'
root="$HOME/tradeops-acceptance-results/f1-68/$sha"
mkdir -p "$root" "$HOME/tradeops-acceptance-results/.scratch"
tmp="$(mktemp -d "$HOME/tradeops-acceptance-results/.scratch/f1-68-XXXXXXXX")"
source_dir="$tmp/source"
worktree_created=0
cleanup(){
 if ((worktree_created)); then git -C "$repo" worktree remove --force "$source_dir" >/dev/null 2>&1 || :; fi
 rm -rf -- "$tmp"
}
trap cleanup EXIT
current="$(git -C "$repo" rev-parse "refs/remotes/origin/$branch")"
[[ "$current" == "$sha" ]] || { echo "STALE_SHA: $current"; exit 3; }
[[ -f "$van_src/frontend/dist/deribit.html" ]] || { echo "VAN_REFERENCE_MISSING"; exit 4; }
git -C "$repo" worktree add --detach "$source_dir" "$sha" >"$root/git-worktree.log" 2>&1
worktree_created=1
[[ "$(git -C "$source_dir" rev-parse HEAD)" == "$sha" ]] || exit 5
mkdir -p "$tmp/cache" "$tmp/home" "$root/screenshots"
cp -a "$van_src/frontend/dist" "$tmp/van"
git -C "$van_src" rev-parse HEAD >"$root/van-reference-sha.txt"
app="$source_dir/apps/trade-terminal"
node_image='node:24.12.0-bookworm-slim'
browser_image='mcr.microsoft.com/playwright:v1.56.0-noble'
docker image inspect "$node_image" >/dev/null 2>&1 || docker pull "$node_image" > "$root/node-pull.log" 2>&1
docker image inspect "$browser_image" >/dev/null 2>&1 || docker pull "$browser_image" > "$root/chromium-pull.log" 2>&1
docker image inspect --format '{{.Id}}' "$node_image" >"$root/node-image.txt"
docker image inspect --format '{{.Id}}' "$browser_image" >"$root/chromium-image.txt"
node_run(){
 docker run --rm --network bridge --user "$(id -u):$(id -g)" \
  --security-opt no-new-privileges --cap-drop ALL -e HOME=/home/local \
  -e NPM_CONFIG_CACHE=/cache -v "$app:/work" -v "$tmp/cache:/cache" \
  -v "$tmp/home:/home/local" -w /work "$node_image" sh -lc "$1"
}
echo "F1-68: build isolated React dist at $sha"
node_run 'npm install --no-package-lock --no-audit --no-fund' >"$root/npm-install.log" 2>&1 || { echo "FAIL npm-install.log"; exit 11; }
node_run 'npm run build' >"$root/react-build.log" 2>&1 || { echo "FAIL react-build.log"; exit 12; }
node_run 'npm install --no-save --no-package-lock --no-audit --no-fund playwright@1.56.0 ws@8.18.3' >"$root/browser-packages.log" 2>&1 || { echo "FAIL browser-packages.log"; exit 13; }
echo "F1-68: capturing real Chromium PNGs on synthetic local fixture"
set +e
docker run --rm --network none --ipc=private --user "$(id -u):$(id -g)" \
 --security-opt no-new-privileges --cap-drop ALL -e HOME=/tmp \
 -v "$app:/work" -v "$tmp/van:/van:ro" -v "$root/screenshots:/evidence" -w /work \
 "$browser_image" node tools/f1-68-local-fixture.mjs \
 --react-root /work/dist --van-root /van --sha "$sha" --out /evidence \
 >"$root/chromium.log" 2>&1
code=$?
set -e
echo "$code" >"$root/chromium-exit.txt"
[[ "$(git -C "$source_dir" rev-parse HEAD)" == "$sha" ]] || { echo STALE_SHA; exit 14; }
[[ "$(git -C "$repo" rev-parse "refs/remotes/origin/$branch")" == "$sha" ]] || { echo STALE_SHA; exit 15; }
python3 - "$root" "$sha" "$code" <<'PY'
import datetime,json,pathlib,sys
root=pathlib.Path(sys.argv[1]);sha=sys.argv[2];rc=int(sys.argv[3])
p=root/'screenshots'/'browser-metrics.json'
records=[]
if p.exists():
 try:records=json.loads(p.read_text())['results']
 except Exception:pass
images=sorted(x.name for x in (root/'screenshots').glob('*.png'))
summary={'issue':68,'pr':63,'sha':sha,
 'generated_utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),
 'fixture':'OFFLINE_SYNTHETIC_ONLY_NOT_LIVE_DERIBIT',
 'browser_exit':rc,'browser_records':records,
 'screenshots':images,'screenshots_count':len(images),
 'capture_status':'CAPTURED' if rc==0 and len(records)==4 and len(images)>=8 else 'FAIL',
 'visual_parity':'PENDING_ORCHESTRATOR_REVIEW',
 'ci01_command_gates':'SEE_CI01_PR63_SUMMARY',
 'files':['screenshots/browser-metrics.json','screenshots/parity-browser-report.md','chromium.log','react-build.log']}
(root/'summary.json').write_text(json.dumps(summary,ensure_ascii=False,indent=2)+'\n')
print(json.dumps({'sha':sha,'capture':summary['capture_status'],
 'png_count':len(images),'evidence':str(root)},ensure_ascii=False))
PY
[[ "$code" == 0 ]] || { echo "F1-68 Chromium FAILED, see $root/chromium.log"; exit 16; }
echo "F1-68 evidence: $root/"
