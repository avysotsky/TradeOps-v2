#!/usr/bin/env bash
# Run as root inside the owner's Ubuntu; never changes Docker/WSL/global configuration.
# Keeps a separate bounded test server across invocations; never uses dev-infra-postgres.
set -Eeuo pipefail
repo=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
suite=${1:-b2}
case "$suite" in
  b2) filter='FullyQualifiedName~OmsJournalPostgresTests|FullyQualifiedName~OmsJournalContractTests'; expected=28 ;;
  b3) filter='FullyQualifiedName~OmsPrivateEventContractTests|FullyQualifiedName~OmsPrivateCapturePostgresTests'; expected=28 ;;
  b3-stream) filter='FullyQualifiedName~DeribitPrivateCaptureTests|FullyQualifiedName~DeribitPrivateCaptureParserTests|FullyQualifiedName~OmsPrivateCaptureCoordinatorTests|FullyQualifiedName~DeribitPrivateSocketTests|FullyQualifiedName~DeribitPrivateStreamPostgresTests|FullyQualifiedName~DeribitAdapterTests|FullyQualifiedName~DeribitWebSocketMessageParserTests|FullyQualifiedName=TradeOps.UnitTests.ExchangeServiceRegistrationTests.AddTradeOpsExchange_SelectsDeribitTestnetFromConfiguration|FullyQualifiedName=TradeOps.UnitTests.ExchangeServiceRegistrationTests.AddTradeOpsExchange_DeribitProductionBaseUrlIsRejectedOnResolution'; expected=52 ;;
  fixture) filter='FullyQualifiedName~OmsPostgresFixtureTests'; expected=4 ;;
  *) echo 'Usage: oms-postgres.sh [b2|b3|b3-stream|fixture]'; exit 2 ;;
esac
[[ $(id -u) == 0 ]] || { echo 'Run with wsl.exe -d Ubuntu -u root -- bash <script>'; exit 2; }
owner=tradeops-tests-v1
container=tradeops-tests-v1-pg17
network=tradeops-tests-v1-net
image=postgres:17.11-bookworm
init="$repo/scripts/testing/postgres-test-init.sql"
out="$repo/.tradeops/postgres-reuse/$(date -u +%Y%m%dT%H%M%S)-$suite-$RANDOM"
mkdir -p "$out"
guard() { runuser -u user -- /home/user/.local/bin/wsl-resource-guard; }
snapshot() {
  for id in $(docker ps -aq); do
    [[ $(docker inspect --format '{{.Name}}' "$id") == "/$container" ]] && continue
    docker inspect --format '{{.Id}} {{.Name}} {{.State.Status}} {{.State.StartedAt}} {{.RestartCount}} {{if .State.Health}}{{.State.Health.Status}}{{end}}' "$id"
  done | sort
}
guard | tee "$out/guard-before.txt"
snapshot > "$out/docker-before.txt"
finish() {
  local result=$?
  trap - EXIT
  snapshot > "$out/docker-after.txt"
  if ! diff -u "$out/docker-before.txt" "$out/docker-after.txt" > "$out/docker-diff.txt"; then
    echo 'FAIL: existing container state changed; inspect evidence'; result=1
  fi
  guard > "$out/guard-after.txt" || result=1
  python3 - "$out/summary.json" "$result" <<'PY'
import json,sys
from pathlib import Path
p=Path(sys.argv[1])
if p.exists():
    data=json.loads(p.read_text())
    data['environment_status']='PASS' if sys.argv[2]=='0' else 'FAIL'
    if sys.argv[2]!='0': data['status']='FAIL'
    p.write_text(json.dumps(data,indent=2)+'\n')
PY
  echo "EVIDENCE=$out"
  exit "$result"
}
trap finish EXIT
docker image inspect "$image" >/dev/null # no install or pull
if ! docker network inspect "$network" >/dev/null 2>&1; then
  docker network create --internal --label "tradeops.tests.owner=$owner" "$network" >/dev/null
fi
docker network inspect "$network" | python3 -c '
import json,sys
n=json.load(sys.stdin)[0]
assert n["Name"]=="tradeops-tests-v1-net" and n["Internal"] and n["Labels"].get("tradeops.tests.owner")=="tradeops-tests-v1", "UNOWNED_TEST_NETWORK"
assert all(c["Name"]=="tradeops-tests-v1-pg17" for c in n.get("Containers",{}).values()), "FOREIGN_NETWORK_MEMBER"
'
if ! docker container inspect "$container" >/dev/null 2>&1; then
  password=$(cat /proc/sys/kernel/random/uuid)
  export POSTGRES_PASSWORD="$password"
  docker create --name "$container" --label "tradeops.tests.owner=$owner" \
    --network "$network" --memory 384m --cpus 0.5 --pids-limit 128 \
    --mount type=tmpfs,destination=/var/lib/postgresql/data,tmpfs-size=268435456 \
    --mount "type=bind,source=$init,target=/docker-entrypoint-initdb.d/10-tradeops.sql,readonly" \
    -e POSTGRES_PASSWORD -e POSTGRES_USER=tradeops_test_admin -e POSTGRES_DB=postgres \
    -e POSTGRES_INITDB_ARGS='--encoding=UTF8 --locale=C --auth-host=scram-sha-256' \
    "$image" -c cluster_name=tradeops_tests_v1 -c shared_buffers=16MB \
    -c max_connections=30 -c max_wal_size=64MB -c min_wal_size=32MB >/dev/null
  unset POSTGRES_PASSWORD password
fi
# Validate before even starting a pre-existing container. Never print inspect Env/secrets.
image_id=$(docker image inspect --format '{{.Id}}' "$image")
docker inspect "$container" | python3 -c '
import json,sys
c=json.load(sys.stdin)[0]; h=c["HostConfig"]
assert c["Name"]=="/tradeops-tests-v1-pg17" and c["Config"]["Labels"].get("tradeops.tests.owner")=="tradeops-tests-v1", "UNOWNED_TEST_CONTAINER"
assert c["Image"]==sys.argv[1] and h["NetworkMode"]=="tradeops-tests-v1-net", "TEST_IMAGE_OR_NETWORK_MISMATCH"
assert h["Memory"]==402653184 and h["NanoCpus"]==500000000 and h["PidsLimit"]==128, "TEST_RESOURCE_LIMIT_MISMATCH"
assert not h.get("PortBindings") and not h["Privileged"] and not h.get("Binds"), "UNEXPECTED_TEST_EXPOSURE"
assert len(h["Mounts"])==2, "UNEXPECTED_TEST_MOUNTS"
tmp=next(m for m in h["Mounts"] if m["Type"]=="tmpfs")
assert tmp["Target"]=="/var/lib/postgresql/data" and tmp["TmpfsOptions"]["SizeBytes"]==268435456, "TEST_DISK_BOUND_MISMATCH"
bind=next(m for m in h["Mounts"] if m["Type"]=="bind")
assert bind["Source"]==sys.argv[2] and bind["Target"]=="/docker-entrypoint-initdb.d/10-tradeops.sql" and bind["ReadOnly"], "UNEXPECTED_TEST_BIND"
assert c["Config"]["Cmd"]==["-c","cluster_name=tradeops_tests_v1","-c","shared_buffers=16MB","-c","max_connections=30","-c","max_wal_size=64MB","-c","min_wal_size=32MB"], "UNEXPECTED_TEST_CONFIGURATION"
assert c["Config"]["Entrypoint"]==["docker-entrypoint.sh"], "UNEXPECTED_TEST_ENTRYPOINT"
env=dict(x.split("=",1) for x in c["Config"]["Env"])
assert env.get("POSTGRES_USER")=="tradeops_test_admin" and env.get("POSTGRES_DB")=="postgres" and env.get("POSTGRES_INITDB_ARGS")=="--encoding=UTF8 --locale=C --auth-host=scram-sha-256", "UNEXPECTED_TEST_INIT"
assert "POSTGRES_HOST_AUTH_METHOD" not in env and "PGOPTIONS" not in env, "UNEXPECTED_TEST_AUTH_OVERRIDE"
' "$image_id" "$init"
if [[ $(docker inspect --format '{{.State.Status}}' "$container") != running ]]; then
  docker start "$container" >/dev/null
fi
ready=false
for attempt in $(seq 1 45); do
  if docker exec "$container" pg_isready -U tradeops_test_admin -d postgres >/dev/null 2>&1; then ready=true; break; fi
  sleep 1
done
[[ "$ready" == true ]] || { echo 'TEST_POSTGRES_NOT_READY'; exit 1; }
password=$(docker inspect "$container" | python3 -c 'import json,sys; print(next(x.split("=",1)[1] for x in json.load(sys.stdin)[0]["Config"]["Env"] if x.startswith("POSTGRES_PASSWORD=")))')
ip=$(docker inspect --format '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' "$container")
[[ "$ip" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo 'TEST_NETWORK_ADDRESS_MISSING'; exit 1; }
connection="Host=$ip;Port=5432;Database=postgres;Username=tradeops_test_admin;Password=$password;Pooling=false"
unset password
cpu=$(awk '/Cpus_allowed_list/ {print $2}' /proc/self/status)
cpu=${cpu%%[-,]*}
git -c safe.directory="$repo" -C "$repo" rev-parse HEAD > "$out/source-sha.txt"
git -c safe.directory="$repo" -C "$repo" diff --binary HEAD -- src/TradeOps.Application/Oms src/TradeOps.Infrastructure/Persistence src/TradeOps.Infrastructure/Exchange/Deribit tests/TradeOps.UnitTests scripts/testing > "$out/source.patch"
python3 - "$repo" "$out" <<'PY'
import sys,subprocess,hashlib,json
from pathlib import Path
repo=Path(sys.argv[1]); out=Path(sys.argv[2])
paths=['src/TradeOps.Application/Oms','src/TradeOps.Infrastructure/Persistence','src/TradeOps.Infrastructure/Exchange/Deribit','tests/TradeOps.UnitTests','scripts/testing']
files=subprocess.check_output(['git','-c','safe.directory='+str(repo),'-C',str(repo),'ls-files','-z','--',*paths]).decode().split('\0')
manifest={p:hashlib.sha256((repo/p).read_bytes().replace(b'\r\n',b'\n')).hexdigest() for p in files if p}
(out/'source-manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
PY
sha256sum "$repo/tests/TradeOps.UnitTests/bin/Release/net10.0/TradeOps.UnitTests.dll" > "$out/test-assembly-sha256.txt"
docker inspect --format '{{.Id}} {{.State.StartedAt}} {{.RestartCount}}' "$container" > "$out/test-server.txt"
docker stats --no-stream --format '{{.Name}} {{.CPUPerc}} {{.MemUsage}} {{.PIDs}}' "$container" > "$out/resources-before.txt"
cd /tmp # use installed Ubuntu SDK outside global.json
set +e
taskset -c "$cpu" runuser -u user -- env \
  -u TRADEOPS_TEST_POSTGRES_ADMIN -u GITHUB_ACTIONS \
  DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_PROCESSOR_COUNT=1 \
  DeribitTerminal__ExecutionEnabled=false DeribitTerminal__MainnetExecutionEnabled=false \
  TRADEOPS_B2_POSTGRES="$connection" \
  dotnet vstest "$repo/tests/TradeOps.UnitTests/bin/Release/net10.0/TradeOps.UnitTests.dll" \
  --Settings:"$repo/scripts/testing/oms-postgres.runsettings" \
  --TestCaseFilter:"$filter" --Logger:'trx;LogFileName=results.trx' \
  --ResultsDirectory:"$out" > "$out/test.log" 2>&1
result=$?
set -e
unset connection
tail -n 15 "$out/test.log"
docker stats --no-stream --format '{{.Name}} {{.CPUPerc}} {{.MemUsage}} {{.PIDs}}' "$container" > "$out/resources-after.txt"
docker exec "$container" psql -X -U tradeops_test_admin -d postgres -At -c \
  "SELECT datname FROM pg_database ORDER BY datname; SELECT rolname FROM pg_roles WHERE rolname ~ '^to_app_'; SELECT count(*) FROM pg_stat_activity WHERE datname LIKE 'to_%';" > "$out/database-after.txt"
python3 - "$out" "$expected" "$result" <<'PY'
import json,sys,xml.etree.ElementTree as ET
from pathlib import Path
out=Path(sys.argv[1]); root=ET.parse(out/'results.trx').getroot(); ns={'t':root.tag.split('}')[0][1:]}
counters=root.find('t:ResultSummary/t:Counters',ns).attrib
cases=root.findall('t:Results/t:UnitTestResult',ns)
def seconds(value):
    h,m,s=value.split(':'); return int(h)*3600+int(m)*60+float(s)
summary={'counters':counters,'case_seconds':sum(seconds(x.attrib['duration']) for x in cases),
         'pg_case_seconds':sum(seconds(x.attrib['duration']) for x in cases if any(n in x.attrib['testName'] for n in ['OmsJournalPostgresTests','OmsPrivateCapturePostgresTests','DeribitPrivateStreamPostgresTests'])),
         'test_server_retained':True}
ok=int(sys.argv[3])==0 and len(cases)==int(sys.argv[2]) and int(counters['passed'])==int(sys.argv[2]) and all(x.attrib['outcome']=='Passed' for x in cases)
db=(out/'database-after.txt').read_text().splitlines()
clean=not any(x.startswith(('to_test_','to_app_')) for x in db) and sum(x.startswith('to_tpl_') for x in db)<=1 and db[-1]=='0'
summary['cleanup_status']='PASS' if clean else 'FAIL'
ok=ok and clean
summary['status']='PASS' if ok else 'FAIL'
(out/'summary.json').write_text(json.dumps(summary,indent=2)+'\n')
print(json.dumps(summary))
assert ok, 'ACTUAL_TEST_COUNTS_OR_STATUSES_FAILED'
PY
exit "$result"
