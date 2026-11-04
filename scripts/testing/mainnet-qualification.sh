#!/usr/bin/env bash
# Separate durable owner-marked store; never reuses or removes another project's database.
set -Eeuo pipefail
repo=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
mode=${1:-store-check}
[[ "$mode" == store-check || "$mode" == run ]] || exit 2
[[ $(id -u) == 0 ]] || exit 2
owner=tradeops-mainnet-qualification-v1
container=tradeops-mainnet-qualification-pg17
network=tradeops-mainnet-qualification-net
volume=tradeops-mainnet-qualification-data-v1
image=postgres:17.11-bookworm
out="$repo/.tradeops/mainnet-qualification/$(date -u +%Y%m%dT%H%M%S)-$mode-$RANDOM"
server_validated=false
mkdir -p "$out"
guard() { runuser -u user -- /home/user/.local/bin/wsl-resource-guard; }
snapshot() { docker inspect $(docker ps -aq) | python3 -c 'import json,sys; print("\n".join(sorted(" ".join(str(v) for v in [x["Id"],x["Name"],x["State"]["Status"],x["State"]["StartedAt"],x["RestartCount"],x["State"].get("Health",{}).get("Status","")]) for x in json.load(sys.stdin) if x["Name"]!="/tradeops-mainnet-qualification-pg17")))'; }
guard > "$out/guard-before.txt"
snapshot > "$out/docker-before.txt"
finish() {
  local result=$?
  trap - EXIT
  snapshot > "$out/docker-after.txt"
  diff -u "$out/docker-before.txt" "$out/docker-after.txt" > "$out/docker-diff.txt" || result=1
  guard > "$out/guard-after.txt" || result=1
  if [[ "$server_validated" == true && "$result" == 0 ]]; then
    docker exec "$container" psql -X -U tradeops_qualification_admin -d tradeops_mainnet_qualification -At -c \
      "SELECT json_build_object('Commands',(SELECT count(*) FROM oms_commands),'Attempts',(SELECT count(*) FROM oms_attempts),'InboxRows',(SELECT count(*) FROM oms_inbox),'Observations',(SELECT count(*) FROM oms_private_capture_observations),'Incidents',(SELECT count(*) FROM oms_integrity_incidents));" > "$out/database-after.json" || result=1
  fi
  python3 - "$out" "$result" <<'PY'
import json,sys
from pathlib import Path
out=Path(sys.argv[1]);good=sys.argv[2]=='0'
try:r=json.loads((out/'tool.log').read_text())
except Exception:r=dict(Status='MAINNET_ETH_FAILED_UNKNOWN',PlacementAttempts=None,CancellationAttempts=None,ExchangeOrderResolved=False,TradingReady=False)
environment=not (out/'docker-diff.txt').read_text().strip() and 'RESULT: OK' in (out/'guard-after.txt').read_text()
r.update(EnvironmentStatus='PASS' if environment else 'FAIL',EvidenceRelativeDirectory='.tradeops/mainnet-qualification/'+out.name)
if r.get('PersistentStore') is True:r['JournalRetained']=True
if not good and r.get('Status')=='MAINNET_ETH_CAPTURE_PASS':r['Status']='MAINNET_ETH_CAPTURE_FAILED'
if (out/'database-after.json').exists():
    try:r['DatabaseAudit']=json.loads((out/'database-after.json').read_text())
    except Exception:good=False
r['CaptureSourceCommit']=(out/'source-sha.txt').read_text().strip() if (out/'source-sha.txt').exists() else None
(out/'summary.json').write_text(json.dumps(r,indent=2)+'\n')
print('TRADEOPS_MAINNET_ETH_RESULT='+json.dumps(r,separators=(',',':')))
sys.exit(0 if good and environment else 1)
PY
  exit "$?"
}
trap finish EXIT
image_id=$(docker image inspect --format '{{.Id}}' "$image")
if ! docker network inspect "$network" >/dev/null 2>&1; then docker network create --internal --label "tradeops.qualification.owner=$owner" "$network" >/dev/null; fi
docker network inspect "$network" | python3 -c 'import json,sys;n=json.load(sys.stdin)[0];assert n["Internal"] and n["Labels"].get("tradeops.qualification.owner")=="tradeops-mainnet-qualification-v1";assert all(c["Name"]=="tradeops-mainnet-qualification-pg17" for c in n.get("Containers",{}).values())'
if ! docker volume inspect "$volume" >/dev/null 2>&1; then docker volume create --label "tradeops.qualification.owner=$owner" "$volume" >/dev/null; fi
docker volume inspect "$volume" | python3 -c 'import json,sys;v=json.load(sys.stdin)[0];assert v["Name"]=="tradeops-mainnet-qualification-data-v1" and v["Labels"].get("tradeops.qualification.owner")=="tradeops-mainnet-qualification-v1"'
if ! docker container inspect "$container" >/dev/null 2>&1; then
  export POSTGRES_PASSWORD=$(cat /proc/sys/kernel/random/uuid)
  docker create --name "$container" --label "tradeops.qualification.owner=$owner" --network "$network" \
    --memory 384m --cpus 0.5 --pids-limit 128 \
    --mount "type=volume,source=$volume,target=/var/lib/postgresql/data" \
    --mount "type=bind,source=$repo/scripts/testing/mainnet-qualification-init.sql,target=/docker-entrypoint-initdb.d/10-owner.sql,readonly" \
    -e POSTGRES_PASSWORD -e POSTGRES_USER=tradeops_qualification_admin -e POSTGRES_DB=tradeops_mainnet_qualification \
    -e POSTGRES_INITDB_ARGS='--encoding=UTF8 --locale=C --auth-host=scram-sha-256' \
    "$image" -c cluster_name=tradeops_mainnet_qualification_v1 -c shared_buffers=16MB -c max_connections=30 -c max_wal_size=64MB -c min_wal_size=32MB >/dev/null
  unset POSTGRES_PASSWORD
fi
docker inspect "$container" | python3 -c '
import json,sys
c=json.load(sys.stdin)[0];h=c["HostConfig"]
assert c["Name"]=="/tradeops-mainnet-qualification-pg17" and c["Config"]["Labels"].get("tradeops.qualification.owner")=="tradeops-mainnet-qualification-v1"
assert c["Image"]==sys.argv[2]
assert h["NetworkMode"]=="tradeops-mainnet-qualification-net" and h["Memory"]==402653184 and h["NanoCpus"]==500000000 and h["PidsLimit"]==128
assert not h.get("PortBindings") and not h["Privileged"] and not h.get("Binds")
assert len(h["Mounts"])==2
v=next(m for m in h["Mounts"] if m["Type"]=="volume"); assert v["Source"]=="tradeops-mainnet-qualification-data-v1" and v["Target"]=="/var/lib/postgresql/data"
b=next(m for m in h["Mounts"] if m["Type"]=="bind"); assert b["Source"]==sys.argv[1] and b["ReadOnly"]
assert c["Config"]["Cmd"]==["-c","cluster_name=tradeops_mainnet_qualification_v1","-c","shared_buffers=16MB","-c","max_connections=30","-c","max_wal_size=64MB","-c","min_wal_size=32MB"]
e=dict(x.split("=",1) for x in c["Config"]["Env"]);assert e["POSTGRES_USER"]=="tradeops_qualification_admin" and e["POSTGRES_DB"]=="tradeops_mainnet_qualification"
' "$repo/scripts/testing/mainnet-qualification-init.sql" "$image_id"
server_validated=true
if [[ $(docker inspect --format '{{.State.Running}}' "$container") != true ]]; then docker start "$container" >/dev/null; fi
for i in $(seq 1 30); do
  if docker exec "$container" pg_isready -U tradeops_qualification_admin -d tradeops_mainnet_qualification >/dev/null; then break; fi
  [[ "$i" != 30 ]] || exit 1
  sleep 1
done
git -c safe.directory="$repo" -C "$repo" rev-parse HEAD > "$out/source-sha.txt"
python3 - "$repo" "$out" <<'PY'
import sys,json,hashlib,subprocess
from pathlib import Path
repo=Path(sys.argv[1]);out=Path(sys.argv[2])
paths=['AGENTS.md','TradeOps.sln','src/TradeOps.Application/Oms','src/TradeOps.Domain/Oms','src/TradeOps.Connectors.Deribit','src/TradeOps.Infrastructure/Persistence','src/TradeOps.Infrastructure/TradeOps.Infrastructure.csproj','tools/TradeOps.DeribitPrivateSessionProbe','tools/TradeOps.DeribitLiveInboxQualification','tools/TradeOps.DeribitTestnetOrderQualification','tools/TradeOps.DeribitMainnetEthQualification','scripts/testing','scripts/deribit/Deribit-MainnetEthLocal.ps1']
files=subprocess.check_output(['git','-c','safe.directory='+str(repo),'-C',str(repo),'ls-files','-z','--',*paths]).decode().split('\0')
(out/'source-manifest.json').write_text(json.dumps({p:hashlib.sha256((repo/p).read_bytes().replace(b'\r\n',b'\n')).hexdigest() for p in files if p},indent=2)+'\n')
PY
sha256sum "$repo/tools/TradeOps.DeribitMainnetEthQualification/bin/Release/net10.0/TradeOps.DeribitMainnetEthQualification.dll" > "$out/tool-assembly-sha256.txt"
docker inspect --format '{{.Id}} {{.State.StartedAt}} {{.RestartCount}}' "$container" > "$out/server.txt"
connection=$(docker inspect "$container" | python3 -c 'import json,sys;c=json.load(sys.stdin)[0];e=dict(x.split("=",1) for x in c["Config"]["Env"]);n=c["NetworkSettings"]["Networks"]["tradeops-mainnet-qualification-net"];print("Host="+n["IPAddress"]+";Port=5432;Database=tradeops_mainnet_qualification;Username=tradeops_qualification_admin;Password="+e["POSTGRES_PASSWORD"]+";Include Error Detail=false;Timeout=10;Command Timeout=30")')
export TRADEOPS_MAINNET_QUALIFICATION_POSTGRES="$connection"
export TRADEOPS_MAINNET_ATTEMPT_DIRECTORY="$repo/.tradeops/b3-mainnet-small-account/attempt"
cpu=$(awk '/Cpus_allowed_list/ {print $2}' /proc/self/status);cpu=${cpu%%[-,]*}
args=();[[ "$mode" != store-check ]] || args=(--store-check)
cd /tmp
set +e
taskset -c "$cpu" runuser -u user -- env DOTNET_PROCESSOR_COUNT=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 \
  DeribitTerminal__ExecutionEnabled=false DeribitTerminal__MainnetExecutionEnabled=false \
  dotnet "$repo/tools/TradeOps.DeribitMainnetEthQualification/bin/Release/net10.0/TradeOps.DeribitMainnetEthQualification.dll" "${args[@]}" > "$out/tool.log" 2>&1
result=$?
unset TRADEOPS_MAINNET_QUALIFICATION_POSTGRES TRADEOPS_MAINNET_ATTEMPT_DIRECTORY connection
exit "$result"
