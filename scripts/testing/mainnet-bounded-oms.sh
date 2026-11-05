#!/usr/bin/env bash
# OWNER-LAUNCHED order scenario. Never invoked by the coding agent or test runner.
# Existing retained owned store only: no creates/start/migrations/grants/retries.
set -Eeuo pipefail
repo=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
[[ ${1:-} == owner-run && $(id -u) == 0 ]] || exit 2
container=tradeops-mainnet-qualification-pg17
out="$repo/.tradeops/bounded-oms/$(date -u +%Y%m%dT%H%M%S)-$RANDOM"
mkdir -p "$out"
guard() { runuser -u user -- /home/user/.local/bin/wsl-resource-guard; }
snapshot() { docker inspect $(docker ps -aq) | python3 -c 'import json,sys;print("\n".join(sorted(" ".join(str(v) for v in [x["Id"],x["Name"],x["State"]["Status"],x["State"]["StartedAt"],x["RestartCount"]]) for x in json.load(sys.stdin))))'; }
audit() {
 docker exec "$container" psql -X -U tradeops_qualification_admin -d tradeops_mainnet_qualification -At -c \
 "BEGIN READ ONLY; SELECT json_build_object('Commands',(SELECT count(*) FROM oms_commands),'Attempts',(SELECT count(*) FROM oms_attempts),'InboxRows',(SELECT count(*) FROM oms_inbox),'Observations',(SELECT count(*) FROM oms_private_capture_observations),'Incidents',(SELECT count(*) FROM oms_integrity_incidents)); COMMIT;" | sed '/^BEGIN$/d;/^COMMIT$/d'
}
guard > "$out/guard-before.txt"
snapshot > "$out/docker-before.txt"
validated=false
finish() {
 local result=$?
 trap - EXIT
 unset TRADEOPS_BOUNDED_OMS_JOURNAL_POSTGRES TRADEOPS_BOUNDED_OMS_CAPTURE_POSTGRES TRADEOPS_BOUNDED_OMS_READER_POSTGRES
 if [[ "$validated" == true ]]; then audit > "$out/database-after.json" || result=1; fi
 snapshot > "$out/docker-after.txt" || result=1
 diff -u "$out/docker-before.txt" "$out/docker-after.txt" > "$out/docker-diff.txt" || result=1
 guard > "$out/guard-after.txt" || result=1
 echo "BOUNDED_OMS_EVIDENCE=.tradeops/bounded-oms/$(basename "$out") EXIT=$result"
 exit "$result"
}
trap finish EXIT
mapfile -t connections < <(docker inspect "$container" | python3 -c '
import json,sys,hmac,hashlib
c=json.load(sys.stdin)[0];h=c["HostConfig"];owner="tradeops-mainnet-qualification-v1"
assert c["Name"]=="/tradeops-mainnet-qualification-pg17" and c["State"]["Running"]
assert c["Config"]["Labels"].get("tradeops.qualification.owner")==owner and c["Config"]["Image"]=="postgres:17.11-bookworm"
assert h["NetworkMode"]=="tradeops-mainnet-qualification-net" and not h.get("PortBindings") and not h["Privileged"]
assert h["Memory"]==402653184 and h["NanoCpus"]==500000000 and h["PidsLimit"]==128 and len(c["Mounts"])==2
v=next(x for x in c["Mounts"] if x["Type"]=="volume")
assert v["Name"]=="tradeops-mainnet-qualification-data-v1" and v["Destination"]=="/var/lib/postgresql/data"
b=next(x for x in c["Mounts"] if x["Type"]=="bind")
assert b["Source"]==sys.argv[1]+"/scripts/testing/mainnet-qualification-init.sql" and not b["RW"]
e=dict(x.split("=",1) for x in c["Config"]["Env"])
assert e["POSTGRES_USER"]=="tradeops_qualification_admin" and e["POSTGRES_DB"]=="tradeops_mainnet_qualification"
ip=c["NetworkSettings"]["Networks"]["tradeops-mainnet-qualification-net"]["IPAddress"]
for role in ["tradeops_q_bounded_journal_v1","tradeops_q_capture_v1","tradeops_q_bounded_reader_v1"]:
 password=hmac.new(e["POSTGRES_PASSWORD"].encode(),role.encode(),hashlib.sha256).hexdigest()
 print("Host="+ip+";Database=tradeops_mainnet_qualification;Username="+role+";Password="+password)
' "$repo")
[[ ${#connections[@]} == 3 ]] || exit 1
docker network inspect tradeops-mainnet-qualification-net | python3 -c 'import json,sys;n=json.load(sys.stdin)[0];assert n["Internal"] and n["Labels"].get("tradeops.qualification.owner")=="tradeops-mainnet-qualification-v1";assert all(c["Name"]=="tradeops-mainnet-qualification-pg17" for c in n.get("Containers",{}).values())'
docker volume inspect tradeops-mainnet-qualification-data-v1 | python3 -c 'import json,sys;assert json.load(sys.stdin)[0]["Labels"].get("tradeops.qualification.owner")=="tradeops-mainnet-qualification-v1"'
audit > "$out/database-before.json"
validated=true
git -c safe.directory="$repo" -C "$repo" rev-parse HEAD > "$out/source-sha.txt"
sha256sum "$repo/tools/TradeOps.DeribitMainnetEthQualification/bin/Release/net10.0/TradeOps.DeribitMainnetEthQualification.dll" > "$out/tool-assembly-sha256.txt"
export TRADEOPS_BOUNDED_OMS_JOURNAL_POSTGRES="${connections[0]}"
export TRADEOPS_BOUNDED_OMS_CAPTURE_POSTGRES="${connections[1]}"
export TRADEOPS_BOUNDED_OMS_READER_POSTGRES="${connections[2]}"
export TRADEOPS_BOUNDED_OMS_DIRECTORY="$repo/.tradeops/bounded-oms/runtime"
unset connections
cpu=$(awk '/Cpus_allowed_list/ {print $2}' /proc/self/status);cpu=${cpu%%[-,]*}
cd /tmp
set +e
taskset -c "$cpu" runuser -u user -- env DOTNET_PROCESSOR_COUNT=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 \
 DeribitTerminal__ExecutionEnabled=false DeribitTerminal__MainnetExecutionEnabled=false \
 dotnet "$repo/tools/TradeOps.DeribitMainnetEthQualification/bin/Release/net10.0/TradeOps.DeribitMainnetEthQualification.dll" --owner-bounded-oms > "$out/result.json"
result=$?
set -e
cat "$out/result.json"
exit "$result"
