#!/usr/bin/env bash
# Existing retained store only. Never creates/starts/migrates a server or invokes trading.
set -Eeuo pipefail
repo=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
mode=${1:?explicit mode required}
[[ "$mode" == --read-snapshot || "$mode" == --provision-reader ]] || exit 2
[[ $(id -u) == 0 ]] || exit 2
container=tradeops-mainnet-qualification-pg17
out="$repo/.tradeops/mainnet-recovery/$(date -u +%Y%m%dT%H%M%S)-${mode#--}-$RANDOM"
mkdir -p "$out"
validated=false
guard() { runuser -u user -- /home/user/.local/bin/wsl-resource-guard; }
audit() {
  docker exec "$container" psql -X -U tradeops_qualification_admin -d tradeops_mainnet_qualification -At -c \
    "BEGIN READ ONLY; SELECT json_build_object('Commands',(SELECT count(*) FROM oms_commands),'Attempts',(SELECT count(*) FROM oms_attempts),'InboxRows',(SELECT count(*) FROM oms_inbox),'Observations',(SELECT count(*) FROM oms_private_capture_observations),'Incidents',(SELECT count(*) FROM oms_integrity_incidents)); COMMIT;" | sed '/^BEGIN$/d;/^COMMIT$/d'
}
snapshot() { docker inspect $(docker ps -aq) | python3 -c 'import json,sys;print("\n".join(sorted(" ".join(str(v) for v in [x["Id"],x["Name"],x["State"]["Status"],x["State"]["StartedAt"],x["RestartCount"]]) for x in json.load(sys.stdin))))'; }
guard > "$out/guard-before.txt"
snapshot > "$out/docker-before.txt"
finish() {
  local result=$?
  trap - EXIT
  unset TRADEOPS_RECOVERY_POSTGRES
  if [[ "$validated" == true ]]; then
    audit > "$out/database-after.json" || result=1
    diff -u "$out/database-before.json" "$out/database-after.json" > "$out/database-diff.txt" || result=1
  fi
  snapshot > "$out/docker-after.txt" || result=1
  diff -u "$out/docker-before.txt" "$out/docker-after.txt" > "$out/docker-diff.txt" || result=1
  guard > "$out/guard-after.txt" || result=1
  echo "RECOVERY_EVIDENCE=.tradeops/mainnet-recovery/$(basename "$out") EXIT=$result"
  exit "$result"
}
trap finish EXIT
# Inspect in memory; only the connection is returned into a shell variable, never logs.
connection=$(docker inspect "$container" | python3 -c '
import json,sys,hmac,hashlib
c=json.load(sys.stdin)[0];h=c["HostConfig"];owner="tradeops-mainnet-qualification-v1"
assert c["Name"]=="/tradeops-mainnet-qualification-pg17" and c["State"]["Running"]
assert c["Config"]["Labels"].get("tradeops.qualification.owner")==owner
assert c["Config"]["Image"]=="postgres:17.11-bookworm"
assert h["NetworkMode"]=="tradeops-mainnet-qualification-net" and not h.get("PortBindings") and not h["Privileged"]
assert h["Memory"]==402653184 and h["NanoCpus"]==500000000 and h["PidsLimit"]==128
assert len(c["Mounts"])==2
v=next(x for x in c["Mounts"] if x["Type"]=="volume")
assert v["Name"]=="tradeops-mainnet-qualification-data-v1" and v["Destination"]=="/var/lib/postgresql/data"
b=next(x for x in c["Mounts"] if x["Type"]=="bind")
assert b["Source"]==sys.argv[2]+"/scripts/testing/mainnet-qualification-init.sql" and not b["RW"]
e=dict(x.split("=",1) for x in c["Config"]["Env"])
assert e["POSTGRES_USER"]=="tradeops_qualification_admin" and e["POSTGRES_DB"]=="tradeops_mainnet_qualification"
role="tradeops_qualification_admin" if sys.argv[1]=="--provision-reader" else "tradeops_q_recovery_v1"
password=e["POSTGRES_PASSWORD"] if sys.argv[1]=="--provision-reader" else hmac.new(e["POSTGRES_PASSWORD"].encode(),role.encode(),hashlib.sha256).hexdigest()
ip=c["NetworkSettings"]["Networks"]["tradeops-mainnet-qualification-net"]["IPAddress"]
print("Host="+ip+";Database=tradeops_mainnet_qualification;Username="+role+";Password="+password)
' "$mode" "$repo")
docker network inspect tradeops-mainnet-qualification-net | python3 -c 'import json,sys;n=json.load(sys.stdin)[0];assert n["Internal"] and n["Labels"].get("tradeops.qualification.owner")=="tradeops-mainnet-qualification-v1";assert all(c["Name"]=="tradeops-mainnet-qualification-pg17" for c in n.get("Containers",{}).values())'
docker volume inspect tradeops-mainnet-qualification-data-v1 | python3 -c 'import json,sys;v=json.load(sys.stdin)[0];assert v["Labels"].get("tradeops.qualification.owner")=="tradeops-mainnet-qualification-v1"'
audit > "$out/database-before.json"
validated=true
git -c safe.directory="$repo" -C "$repo" rev-parse HEAD > "$out/source-sha.txt"
sha256sum "$repo/tools/TradeOps.DeribitMainnetEthQualification/bin/Release/net10.0/TradeOps.DeribitMainnetEthQualification.dll" > "$out/tool-assembly-sha256.txt"
export TRADEOPS_RECOVERY_POSTGRES="$connection"
unset connection
cpu=$(awk '/Cpus_allowed_list/ {print $2}' /proc/self/status);cpu=${cpu%%[-,]*}
cd /tmp
taskset -c "$cpu" runuser -u user -- env DOTNET_PROCESSOR_COUNT=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 \
 DeribitTerminal__ExecutionEnabled=false DeribitTerminal__MainnetExecutionEnabled=false \
 dotnet "$repo/tools/TradeOps.DeribitMainnetEthQualification/bin/Release/net10.0/TradeOps.DeribitMainnetEthQualification.dll" "$mode" > "$out/result.json"
cat "$out/result.json"
