#!/usr/bin/env bash
# Run as root inside the owner's Ubuntu; never changes Docker/WSL/global configuration.
# Keeps a separate bounded test server across invocations; never uses dev-infra-postgres.
set -Eeuo pipefail
repo=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
suite=${1:-targeted}
live_inbox=false
legacy_postgres_enabled=false
tool_project=TradeOps.DeribitLiveInboxQualification
stream_filter='FullyQualifiedName~DeribitPrivateCaptureTests|FullyQualifiedName~DeribitPrivateCaptureParserTests|FullyQualifiedName~OmsPrivateCaptureCoordinatorTests|FullyQualifiedName~DeribitPrivateSocketTests|FullyQualifiedName~DeribitPrivateStreamPostgresTests|FullyQualifiedName~DeribitAdapterTests|FullyQualifiedName~DeribitWebSocketMessageParserTests|FullyQualifiedName=TradeOps.UnitTests.ExchangeServiceRegistrationTests.AddTradeOpsExchange_SelectsDeribitTestnetFromConfiguration|FullyQualifiedName=TradeOps.UnitTests.ExchangeServiceRegistrationTests.AddTradeOpsExchange_DeribitProductionBaseUrlIsRejectedOnResolution'
case "$suite" in
  targeted)
    filter=${2:?Targeted checks require a test filter}
    expected=${3:?Targeted checks require an expected test count}
    [[ -n "$filter" && "$expected" =~ ^[1-9][0-9]*$ ]] || { echo 'Invalid targeted test scope'; exit 2; }
    if [[ "${4:-}" == 'legacy-postgres' ]]; then legacy_postgres_enabled=true;
    elif [[ -n "${4:-}" ]]; then echo 'Invalid targeted PostgreSQL mode'; exit 2; fi
    ;;
  b2) filter='FullyQualifiedName~OmsJournalPostgresTests|FullyQualifiedName~OmsJournalContractTests'; expected=28 ;;
  b4-final|b5-final|b6-final)
    filter='FullyQualifiedName~TradeOps.UnitTests'
    expected=${2:?Final stage acceptance requires the discovered full-suite test count}
    [[ "$expected" =~ ^[1-9][0-9]*$ ]] || exit 2
    legacy_postgres_enabled=true
    ;;
  b3) filter='FullyQualifiedName~OmsPrivateEventContractTests|FullyQualifiedName~OmsPrivateCapturePostgresTests'; expected=28 ;;
  b3-stream) filter=$stream_filter; expected=52 ;;
  b3-host) filter="$stream_filter|FullyQualifiedName~DeribitCaptureHostTests|FullyQualifiedName~DeribitCaptureHostPostgresTests"; expected=83 ;;
  b3-session) filter="$stream_filter|FullyQualifiedName~DeribitCaptureHostTests|FullyQualifiedName~DeribitCaptureHostPostgresTests|FullyQualifiedName~DeribitPrivateSessionProbeTests|FullyQualifiedName~DeribitReadOnlyScopeTests"; expected=108 ;;
  b3-inbox) filter="$stream_filter|FullyQualifiedName~DeribitCaptureHostTests|FullyQualifiedName~DeribitCaptureHostPostgresTests|FullyQualifiedName~DeribitPrivateSessionProbeTests|FullyQualifiedName~DeribitReadOnlyScopeTests|FullyQualifiedName~DeribitLiveInboxQualificationTests|FullyQualifiedName~OmsPostgresFixtureTests"; expected=117 ;;
  b3-live-inbox) live_inbox=true ;;
  b3-testnet-order) filter="$stream_filter|FullyQualifiedName~DeribitCaptureHostTests|FullyQualifiedName~DeribitCaptureHostPostgresTests|FullyQualifiedName~DeribitPrivateSessionProbeTests|FullyQualifiedName~DeribitReadOnlyScopeTests|FullyQualifiedName~DeribitLiveInboxQualificationTests|FullyQualifiedName~OmsPostgresFixtureTests|FullyQualifiedName~DeribitTestnetOrderQualificationTests"; expected=134 ;;
  b3-live-testnet-order) live_inbox=true; tool_project=TradeOps.DeribitTestnetOrderQualification ;;
  b3-mainnet-eth) filter="$stream_filter|FullyQualifiedName~DeribitCaptureHostTests|FullyQualifiedName~DeribitCaptureHostPostgresTests|FullyQualifiedName~DeribitPrivateSessionProbeTests|FullyQualifiedName~DeribitReadOnlyScopeTests|FullyQualifiedName~DeribitLiveInboxQualificationTests|FullyQualifiedName~OmsPostgresFixtureTests|FullyQualifiedName~DeribitTestnetOrderQualificationTests|FullyQualifiedName~DeribitMainnetEthQualificationTests"; expected=144 ;;
  b4-order) filter="$stream_filter|FullyQualifiedName~DeribitCaptureHostTests|FullyQualifiedName~DeribitCaptureHostPostgresTests|FullyQualifiedName~DeribitPrivateSessionProbeTests|FullyQualifiedName~DeribitReadOnlyScopeTests|FullyQualifiedName~DeribitLiveInboxQualificationTests|FullyQualifiedName~OmsPostgresFixtureTests|FullyQualifiedName~DeribitTestnetOrderQualificationTests|FullyQualifiedName~DeribitMainnetEthQualificationTests|FullyQualifiedName~OmsJournalPostgresTests|FullyQualifiedName~OmsJournalContractTests|FullyQualifiedName~OmsPrivateEventContractTests|FullyQualifiedName~OmsPrivateCapturePostgresTests|FullyQualifiedName~DeribitOrderRecovery"; expected=247 ;;
  fixture) filter='FullyQualifiedName~OmsPostgresFixtureTests'; expected=4 ;;
  b4-order-reducer) filter='FullyQualifiedName~DeribitOrderRecovery'; expected=47 ;;
  b4-reader) filter='FullyQualifiedName~DeribitOrderRecovery|FullyQualifiedName~OmsPrivateRecoveryReader'; expected=87 ;;
  b4-binding) filter='FullyQualifiedName~DeribitOrderRecovery|FullyQualifiedName~OmsPrivateRecoveryReader|FullyQualifiedName~OmsRecoveryStoreBinding'; expected=99 ;;
  b4-collector) filter='FullyQualifiedName~DeribitOrderRecovery|FullyQualifiedName~OmsPrivateRecoveryReader|FullyQualifiedName~OmsRecoveryStoreBinding|FullyQualifiedName~DeribitPrivateCaptureParser|FullyQualifiedName~DeribitRecoveryQuery'; expected=137 ;;
  b4-known-orders) filter='FullyQualifiedName~DeribitOrderRecovery|FullyQualifiedName~OmsPrivateRecoveryReader|FullyQualifiedName~OmsRecoveryStoreBinding|FullyQualifiedName~DeribitPrivateCaptureParser|FullyQualifiedName~DeribitRecoveryQuery|FullyQualifiedName~DeribitKnownOrderRead'; expected=173 ;;
  b4-gate) filter='FullyQualifiedName~DeribitOrderRecovery|FullyQualifiedName~OmsPrivateRecoveryReader|FullyQualifiedName~OmsRecoveryStoreBinding|FullyQualifiedName~DeribitPrivateCaptureParser|FullyQualifiedName~DeribitRecoveryQuery|FullyQualifiedName~DeribitKnownOrderRead|FullyQualifiedName~DeribitRecoveryEvidenceGate'; expected=209 ;;
  b4-replay) filter="$stream_filter|FullyQualifiedName~DeribitOrderRecovery|FullyQualifiedName~OmsPrivateRecoveryReader|FullyQualifiedName~OmsRecoveryStoreBinding|FullyQualifiedName~DeribitRecoveryQuery|FullyQualifiedName~DeribitKnownOrderRead|FullyQualifiedName~DeribitRecoveryEvidenceGate|FullyQualifiedName~OmsPrivateCaptureReplay"; expected=275 ;;
  b4-replay-gate) filter="$stream_filter|FullyQualifiedName~DeribitOrderRecovery|FullyQualifiedName~OmsPrivateRecoveryReader|FullyQualifiedName~OmsRecoveryStoreBinding|FullyQualifiedName~DeribitRecoveryQuery|FullyQualifiedName~DeribitKnownOrderRead|FullyQualifiedName~DeribitRecoveryEvidenceGate|FullyQualifiedName~OmsPrivateCaptureReplay|FullyQualifiedName~DeribitRecoveryReplayGate"; expected=294 ;;
  b4-account) filter="$stream_filter|FullyQualifiedName~DeribitOrderRecovery|FullyQualifiedName~OmsPrivateRecoveryReader|FullyQualifiedName~OmsRecoveryStoreBinding|FullyQualifiedName~DeribitRecoveryQuery|FullyQualifiedName~DeribitKnownOrderRead|FullyQualifiedName~DeribitRecoveryEvidenceGate|FullyQualifiedName~OmsPrivateCaptureReplay|FullyQualifiedName~DeribitRecoveryReplayGate|FullyQualifiedName~DeribitAccountSnapshot"; expected=336 ;;
  b4-account-host) filter="$stream_filter|FullyQualifiedName~DeribitOrderRecovery|FullyQualifiedName~OmsPrivateRecoveryReader|FullyQualifiedName~OmsRecoveryStoreBinding|FullyQualifiedName~DeribitRecoveryQuery|FullyQualifiedName~DeribitKnownOrderRead|FullyQualifiedName~DeribitRecoveryEvidenceGate|FullyQualifiedName~OmsPrivateCaptureReplay|FullyQualifiedName~DeribitRecoveryReplayGate|FullyQualifiedName~DeribitAccountSnapshot|FullyQualifiedName~DeribitAccountQualification"; expected=364 ;;
  b4-account-manifest) filter="$stream_filter|FullyQualifiedName~DeribitOrderRecovery|FullyQualifiedName~OmsPrivateRecoveryReader|FullyQualifiedName~OmsRecoveryStoreBinding|FullyQualifiedName~DeribitRecoveryQuery|FullyQualifiedName~DeribitKnownOrderRead|FullyQualifiedName~DeribitRecoveryEvidenceGate|FullyQualifiedName~OmsPrivateCaptureReplay|FullyQualifiedName~DeribitRecoveryReplayGate|FullyQualifiedName~DeribitAccountSnapshot|FullyQualifiedName~DeribitAccountQualification|FullyQualifiedName~DeribitEthFutureManifest"; expected=394 ;;
  b4-account-assessment) filter="$stream_filter|FullyQualifiedName~DeribitOrderRecovery|FullyQualifiedName~OmsPrivateRecoveryReader|FullyQualifiedName~OmsRecoveryStoreBinding|FullyQualifiedName~DeribitRecoveryQuery|FullyQualifiedName~DeribitKnownOrderRead|FullyQualifiedName~DeribitRecoveryEvidenceGate|FullyQualifiedName~OmsPrivateCaptureReplay|FullyQualifiedName~DeribitRecoveryReplayGate|FullyQualifiedName~DeribitAccountSnapshot|FullyQualifiedName~DeribitAccountQualification|FullyQualifiedName~DeribitEthFutureManifest|FullyQualifiedName~DeribitRecoveryAccountAssessment"; expected=445 ;;
  b4-unified) filter="$stream_filter|FullyQualifiedName~DeribitOrderRecovery|FullyQualifiedName~OmsPrivateRecoveryReader|FullyQualifiedName~OmsRecoveryStoreBinding|FullyQualifiedName~DeribitRecoveryQuery|FullyQualifiedName~DeribitKnownOrderRead|FullyQualifiedName~DeribitRecoveryEvidenceGate|FullyQualifiedName~OmsPrivateCaptureReplay|FullyQualifiedName~DeribitRecoveryReplayGate|FullyQualifiedName~DeribitAccountSnapshot|FullyQualifiedName~DeribitAccountQualification|FullyQualifiedName~DeribitEthFutureManifest|FullyQualifiedName~DeribitRecoveryAccountAssessment|FullyQualifiedName~DeribitUnifiedRecoveryQualification"; expected=471 ;;
  b4-terminal) filter="$stream_filter|FullyQualifiedName~DeribitOrderRecovery|FullyQualifiedName~OmsPrivateRecoveryReader|FullyQualifiedName~OmsRecoveryStoreBinding|FullyQualifiedName~DeribitRecoveryQuery|FullyQualifiedName~DeribitKnownOrderRead|FullyQualifiedName~DeribitRecoveryEvidenceGate|FullyQualifiedName~OmsPrivateCaptureReplay|FullyQualifiedName~DeribitRecoveryReplayGate|FullyQualifiedName~DeribitAccountSnapshot|FullyQualifiedName~DeribitAccountQualification|FullyQualifiedName~DeribitEthFutureManifest|FullyQualifiedName~DeribitRecoveryAccountAssessment|FullyQualifiedName~DeribitUnifiedRecoveryQualification|FullyQualifiedName~DeribitTerminalEvidence"; expected=521 ;;
  b4-ledger) filter="$stream_filter|FullyQualifiedName~DeribitOrderRecovery|FullyQualifiedName~OmsPrivateRecoveryReader|FullyQualifiedName~OmsRecoveryStoreBinding|FullyQualifiedName~DeribitRecoveryQuery|FullyQualifiedName~DeribitKnownOrderRead|FullyQualifiedName~DeribitRecoveryEvidenceGate|FullyQualifiedName~OmsPrivateCaptureReplay|FullyQualifiedName~DeribitRecoveryReplayGate|FullyQualifiedName~DeribitAccountSnapshot|FullyQualifiedName~DeribitAccountQualification|FullyQualifiedName~DeribitEthFutureManifest|FullyQualifiedName~DeribitRecoveryAccountAssessment|FullyQualifiedName~DeribitUnifiedRecoveryQualification|FullyQualifiedName~DeribitTerminalEvidence|FullyQualifiedName~OmsRecoveryLedger|FullyQualifiedName~DeribitRecoveryLedgerAssessment|FullyQualifiedName~OmsJournalPostgresTests|FullyQualifiedName~OmsJournalContractTests"; expected=596 ;;
  b4-receipts) filter="$stream_filter|FullyQualifiedName~DeribitOrderRecovery|FullyQualifiedName~OmsPrivateRecoveryReader|FullyQualifiedName~OmsRecoveryStoreBinding|FullyQualifiedName~DeribitRecoveryQuery|FullyQualifiedName~DeribitKnownOrderRead|FullyQualifiedName~DeribitRecoveryEvidenceGate|FullyQualifiedName~OmsPrivateCaptureReplay|FullyQualifiedName~DeribitRecoveryReplayGate|FullyQualifiedName~DeribitAccountSnapshot|FullyQualifiedName~DeribitAccountQualification|FullyQualifiedName~DeribitEthFutureManifest|FullyQualifiedName~DeribitRecoveryAccountAssessment|FullyQualifiedName~DeribitUnifiedRecoveryQualification|FullyQualifiedName~DeribitTerminalEvidence|FullyQualifiedName~OmsRecoveryLedger|FullyQualifiedName~DeribitRecoveryLedgerAssessment|FullyQualifiedName~OmsJournalPostgresTests|FullyQualifiedName~OmsJournalContractTests|FullyQualifiedName~DeribitCommandReceipt"; expected=687 ;;
  b4-bounded) filter="$stream_filter|FullyQualifiedName~DeribitOrderRecovery|FullyQualifiedName~OmsPrivateRecoveryReader|FullyQualifiedName~OmsRecoveryStoreBinding|FullyQualifiedName~DeribitRecoveryQuery|FullyQualifiedName~DeribitKnownOrderRead|FullyQualifiedName~DeribitRecoveryEvidenceGate|FullyQualifiedName~OmsPrivateCaptureReplay|FullyQualifiedName~DeribitRecoveryReplayGate|FullyQualifiedName~DeribitAccountSnapshot|FullyQualifiedName~DeribitAccountQualification|FullyQualifiedName~DeribitEthFutureManifest|FullyQualifiedName~DeribitRecoveryAccountAssessment|FullyQualifiedName~DeribitUnifiedRecoveryQualification|FullyQualifiedName~DeribitTerminalEvidence|FullyQualifiedName~OmsRecoveryLedger|FullyQualifiedName~DeribitRecoveryLedgerAssessment|FullyQualifiedName~OmsJournalPostgresTests|FullyQualifiedName~OmsJournalContractTests|FullyQualifiedName~DeribitCommandReceipt|FullyQualifiedName~DeribitBoundedOms"; expected=729 ;;
  *) echo 'Usage: oms-postgres.sh [b2|b3|b3-stream|b3-host|b3-session|b3-inbox|b3-live-inbox|b3-testnet-order|b3-mainnet-eth|b4-order|b4-order-reducer|b4-reader|fixture]'; exit 2 ;;
esac
[[ $(id -u) == 0 ]] || { echo 'Run with wsl.exe -d Ubuntu -u root -- bash <script>'; exit 2; }
owner=tradeops-tests-v1
container=tradeops-tests-v1-pg17
network=tradeops-tests-v1-net
image=postgres:17.11-bookworm
server_validated=false
init="$repo/scripts/testing/postgres-test-init.sql"
out="$repo/.tradeops/postgres-reuse/$(date -u +%Y%m%dT%H%M%S)-$suite-$RANDOM"
mkdir -p "$out"
guard() { runuser -u user -- /home/user/.local/bin/wsl-resource-guard; }
snapshot() {
  docker inspect $(docker ps -aq) | python3 -c 'import json,sys; print("\n".join(sorted(" ".join(str(v) for v in [x["Id"],x["Name"],x["State"]["Status"],x["State"]["StartedAt"],x["RestartCount"],x["State"].get("Health",{}).get("Status","")]) for x in json.load(sys.stdin) if x["Name"]!="/tradeops-tests-v1-pg17")))'
}
if [[ "$live_inbox" == true ]]; then guard > "$out/guard-before.txt"; else guard | tee "$out/guard-before.txt"; fi
snapshot > "$out/docker-before.txt"
finish() {
  local result=$?
  trap - EXIT
  snapshot > "$out/docker-after.txt"
  if ! diff -u "$out/docker-before.txt" "$out/docker-after.txt" > "$out/docker-diff.txt"; then
    echo 'FAIL: existing container state changed; inspect evidence'; result=1
  fi
  guard > "$out/guard-after.txt" || result=1
  if [[ "$live_inbox" == true ]]; then
    if [[ "$server_validated" == true ]]; then
      docker exec "$container" psql -X -U tradeops_test_admin -d postgres -At -c \
        "SELECT datname FROM pg_database ORDER BY datname; SELECT rolname FROM pg_roles WHERE rolname ~ '^to_'; SELECT count(*) FROM pg_stat_activity WHERE datname LIKE 'to_%';" > "$out/database-after.txt" || result=1
    else
      : > "$out/database-after.txt"; result=1
    fi
    python3 - "$out" "$result" "$suite" <<'PY'
import json,sys
from pathlib import Path
out=Path(sys.argv[1]); good=sys.argv[2]=='0'; testnet=sys.argv[3]=='b3-live-testnet-order'
try: data=json.loads((out/'live-tool.log').read_text())
except Exception:
    data=dict(Status='TESTNET_ORDER_FAILED_UNKNOWN' if testnet else 'LIVE_INBOX_FAILED',Stage='GUARD_OR_TOOL_OUTPUT',DatabaseUsed=None,TradingReady=False)
try:
    db=(out/'database-after.txt').read_text().splitlines()
    clean=all(x in ('postgres','template0','template1') or x.startswith('to_tpl_') for x in db[:-1]) and sum(x.startswith('to_tpl_') for x in db)<=1 and db[-1]=='0'
except Exception: clean=False
data['EnvironmentStatus']='PASS' if good else 'FAIL'; data['CleanupStatus']='PASS' if clean else 'FAIL'
data['EvidenceRelativeDirectory']='.tradeops/postgres-reuse/'+out.name
data['CaptureSourceCommit']=(out/'source-sha.txt').read_text().strip() if (out/'source-sha.txt').exists() else None
ok=good and clean and data['Status'] in (['TESTNET_ORDER_CAPTURE_PASS'] if testnet else ['LIVE_INBOX_CONNECTED_NO_EVENTS','LIVE_INBOX_CAPTURE_PASS'])
if not ok and not testnet: data['Status']='LIVE_INBOX_FAILED'
if testnet and data['Status']=='TESTNET_ORDER_CAPTURE_PASS' and not ok: data['Status']='TESTNET_ORDER_CAPTURE_FAILED'
(out/'summary.json').write_text(json.dumps(data,indent=2)+'\n')
print(('TRADEOPS_TESTNET_ORDER_RESULT=' if testnet else 'TRADEOPS_LIVE_INBOX_RESULT=')+json.dumps(data,separators=(',',':')))
sys.exit(0 if ok else 1)
PY
    exit "$?"
  fi
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
server_validated=true
password=$(docker inspect "$container" | python3 -c 'import json,sys; print(next(x.split("=",1)[1] for x in json.load(sys.stdin)[0]["Config"]["Env"] if x.startswith("POSTGRES_PASSWORD=")))')
ip=$(docker inspect --format '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' "$container")
[[ "$ip" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo 'TEST_NETWORK_ADDRESS_MISSING'; exit 1; }
connection="Host=$ip;Port=5432;Database=postgres;Username=tradeops_test_admin;Password=$password;Pooling=false"
unset password
cpu=$(awk '/Cpus_allowed_list/ {print $2}' /proc/self/status)
cpu=${cpu%%[-,]*}
git -c safe.directory="$repo" -C "$repo" rev-parse HEAD > "$out/source-sha.txt"
capture_paths=(
  TradeOps.sln
  src/TradeOps.Application/TradeOps.Application.csproj
  src/TradeOps.Domain/TradeOps.Domain.csproj
  src/TradeOps.Connectors.Deribit
  src/TradeOps.Worker
  tools/TradeOps.DeribitPrivateSessionProbe
  tools/TradeOps.DeribitLiveInboxQualification
  tools/TradeOps.DeribitTestnetOrderQualification
  tools/TradeOps.DeribitMainnetEthQualification
  tools/TradeOps.OmsPhysicalOwnerProbe
  tools/TradeOps.FixProvisioningPlan
  src/TradeOps.Application/Properties
  apps/TradeOps.TerminalApi/Controllers/DeribitTerminalController.cs
  scripts/deribit/Deribit-MainnetEthLocal.ps1
  scripts/deribit/Deribit-RecoveryCollectionLocal.ps1
  scripts/deribit/Deribit-AccountSnapshotLocal.ps1
  scripts/deribit/Deribit-KnownOrderRecoveryLocal.ps1
  scripts/deribit/Deribit-RecoveryAssessmentLocal.ps1
  scripts/deribit/Deribit-AccountRecoveryLocal.ps1
  scripts/deribit/Deribit-BoundedOmsLocal.ps1
  scripts/deribit/Deribit-FixQualificationLocal.ps1
  scripts/deribit/Deribit-FixConfigurationCheck.ps1
  scripts/deribit/Deribit-TestnetOrderLocal.ps1
  scripts/deribit/Deribit-PrivateSessionReadOnlyLocal.ps1
  src/TradeOps.Infrastructure/TradeOps.Infrastructure.csproj
  src/TradeOps.Infrastructure/Exchange/ExchangeServiceRegistration.cs
  src/TradeOps.Application/Oms
  src/TradeOps.Infrastructure/Persistence
  src/TradeOps.Infrastructure/Exchange/Deribit
  tests/TradeOps.UnitTests
  scripts/testing
)
git -c safe.directory="$repo" -C "$repo" diff --binary HEAD -- "${capture_paths[@]}" > "$out/source.patch"
python3 - "$repo" "$out" "${capture_paths[@]}" <<'PY'
import sys,subprocess,hashlib,json
from pathlib import Path
repo=Path(sys.argv[1]); out=Path(sys.argv[2])
paths=sys.argv[3:]
files=subprocess.check_output(['git','-c','safe.directory='+str(repo),'-C',str(repo),'ls-files','-z','--',*paths]).decode().split('\0')
manifest={p:hashlib.sha256((repo/p).read_bytes().replace(b'\r\n',b'\n')).hexdigest() for p in files if p}
(out/'source-manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
PY
if [[ "$live_inbox" == true ]]; then
  sha256sum "$repo/tools/$tool_project/bin/Release/net10.0/$tool_project.dll" > "$out/tool-assembly-sha256.txt"
else
  sha256sum "$repo/tests/TradeOps.UnitTests/bin/Release/net10.0/TradeOps.UnitTests.dll" > "$out/test-assembly-sha256.txt"
fi
docker inspect --format '{{.Id}} {{.State.StartedAt}} {{.RestartCount}}' "$container" > "$out/test-server.txt"
docker stats --no-stream --format '{{.Name}} {{.CPUPerc}} {{.MemUsage}} {{.PIDs}}' "$container" > "$out/resources-before.txt"
cd /tmp # use installed Ubuntu SDK outside global.json
set +e
if [[ "$live_inbox" == true ]]; then
  export TRADEOPS_B2_POSTGRES="$connection"
  export TRADEOPS_TESTNET_ATTEMPT_DIRECTORY="$repo/.tradeops/b3-testnet-order/attempt"
  taskset -c "$cpu" runuser -u user -- env -u TRADEOPS_TEST_POSTGRES_ADMIN -u GITHUB_ACTIONS \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_PROCESSOR_COUNT=1 \
    DeribitTerminal__ExecutionEnabled=false DeribitTerminal__MainnetExecutionEnabled=false \
    dotnet "$repo/tools/$tool_project/bin/Release/net10.0/$tool_project.dll" \
    > "$out/live-tool.log" 2>&1
  result=$?
  unset TRADEOPS_B2_POSTGRES TRADEOPS_TESTNET_ATTEMPT_DIRECTORY connection
  exit "$result" # finish validates peer containers, guard, clone/role/session cleanup, then emits a redacted frame
fi
legacy_postgres=()
if [[ "$legacy_postgres_enabled" == true ]]; then legacy_postgres=("TRADEOPS_TEST_POSTGRES_ADMIN=$connection"); fi
taskset -c "$cpu" runuser -u user -- env \
  -u TRADEOPS_TEST_POSTGRES_ADMIN -u GITHUB_ACTIONS \
  DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_PROCESSOR_COUNT=1 \
  DeribitTerminal__ExecutionEnabled=false DeribitTerminal__MainnetExecutionEnabled=false \
  ASPNETCORE_TEST_CONTENTROOT_TRADEOPS_API="$repo/src/TradeOps.Api" \
  ASPNETCORE_TEST_CONTENTROOT_TRADEOPS_TERMINALAPI="$repo/apps/TradeOps.TerminalApi" \
  TRADEOPS_B2_POSTGRES="$connection" \
  "${legacy_postgres[@]}" \
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
  "SELECT datname FROM pg_database ORDER BY datname; SELECT rolname FROM pg_roles WHERE rolname ~ '^to_'; SELECT count(*) FROM pg_stat_activity WHERE datname LIKE 'to_%';" > "$out/database-after.txt"
python3 - "$out" "$expected" "$result" <<'PY'
import json,sys,xml.etree.ElementTree as ET
from pathlib import Path
out=Path(sys.argv[1]); root=ET.parse(out/'results.trx').getroot(); ns={'t':root.tag.split('}')[0][1:]}
counters=root.find('t:ResultSummary/t:Counters',ns).attrib
cases=root.findall('t:Results/t:UnitTestResult',ns)
def seconds(value):
    h,m,s=value.split(':'); return int(h)*3600+int(m)*60+float(s)
summary={'counters':counters,'case_seconds':sum(seconds(x.attrib['duration']) for x in cases),
         'pg_case_seconds':sum(seconds(x.attrib['duration']) for x in cases if any(n in x.attrib['testName'] for n in ['OmsJournalPostgresTests','OmsPrivateCapturePostgresTests','DeribitPrivateStreamPostgresTests','DeribitCaptureHostPostgresTests','DeribitLiveInboxQualificationTests','DeribitOrderRecoveryPostgresTests','OmsPrivateRecoveryReaderPostgresTests'])),
         'test_server_retained':True}
ok=int(sys.argv[3])==0 and len(cases)==int(sys.argv[2]) and int(counters['passed'])==int(sys.argv[2]) and all(x.attrib['outcome']=='Passed' for x in cases)
db=(out/'database-after.txt').read_text().splitlines()
clean=all(x in ('postgres','template0','template1') or x.startswith('to_tpl_') for x in db[:-1]) and sum(x.startswith('to_tpl_') for x in db)<=1 and db[-1]=='0'
summary['cleanup_status']='PASS' if clean else 'FAIL'
ok=ok and clean
summary['status']='PASS' if ok else 'FAIL'
(out/'summary.json').write_text(json.dumps(summary,indent=2)+'\n')
print(json.dumps(summary))
assert ok, 'ACTUAL_TEST_COUNTS_OR_STATUSES_FAILED'
PY
exit "$result"
