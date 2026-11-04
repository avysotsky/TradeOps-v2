# Deribit read-only capture host

Opt-in runtime for [B3 private capture](DERIBIT_PRIVATE_CAPTURE_LIFECYCLE.md), within the [target module boundaries](TARGET_PLATFORM_ARCHITECTURE.md). This is a separate composition of the existing Worker executable, not an additional stream inside the legacy worker.

## Selection and trusted binding

`Worker:Mode=DeribitReadOnlyCapture` selects only `DeribitReadOnlyCaptureWorker`. Program returns through this branch before registering legacy workers, `IExchangeClient`, `IExchangeEventStream`, Orders/Fills processors, reconciliation services or alert delivery. No sender, migrations, API/UI endpoint or automatic role provisioning is registered. An absent mode or explicit `Legacy` preserves the previous composition; every unknown mode fails startup rather than falling back. The checked-in application settings do not select the new mode.

Both `DeribitTerminal:ExecutionEnabled` and `DeribitTerminal:MainnetExecutionEnabled` must be explicitly false, including in testnet capture mode. Missing, true or malformed switches fail startup. Capture credentials are separate from the legacy `Exchange:Deribit` section. `ConnectionStrings:TradeOpsCaptureDb` is mandatory; there is no fallback to the legacy database setting.

Server configuration supplies the entire immutable `DeribitPrivateCaptureBinding`: tenant, local account, optional subaccount, Testnet/Mainnet environment, expected remote numeric account ID, expected main/subaccount type and instrument manifest. Official WSS endpoint is derived from the environment; custom endpoints are not accepted by this host. Remote ID/type are checked by the transport before releasing observations. This verifies a server-supplied pin, not an automatic discovery of credential ownership.

The explicit [public instrument manifest preflight](DERIBIT_INSTRUMENT_MANIFEST.md)
can validate configured names and units before private qualification. It does not
generate the account binding, infer subscription currency or turn public metadata
success into account coverage/readiness. It is not enabled automatically by this host.

Illustrative **non-secret** configuration; IDs/metadata are synthetic and must be replaced with verified server mappings before any deployment:

```json
{
  "Worker": { "Mode": "DeribitReadOnlyCapture" },
  "DeribitTerminal": { "ExecutionEnabled": false, "MainnetExecutionEnabled": false },
  "DeribitCapture": {
    "Tenant": "example-tenant",
    "Environment": "Testnet",
    "Account": "example-local-account",
    "ExpectedAccountId": 123456,
    "ExpectedAccountType": "main",
    "Instruments": [
      { "Name": "BTC-PERPETUAL", "Kind": "future", "Currency": "BTC",
        "AmountUnit": "Usd", "AmountCurrency": "USD" }
    ]
  }
}
```

For subaccounts supply `DeribitCapture:Subaccount` and `ExpectedAccountType=subaccount`. Credentials and capture DB connection remain server-side, for example through the secret provider/environment keys `DeribitCapture__ClientId`, `DeribitCapture__ClientSecret`, `ConnectionStrings__TradeOpsCaptureDb`. Do not commit real values, auth frames or private responses. Prefer dedicated read-only credentials; the transport also demands and verifies read-only connection scopes.

The binding is read once. Configuration reload or UI account selection cannot retarget an active process; stop/join the old host and start with a new verified binding. No dynamic account-switch endpoint is exposed. A one-instrument example is not evidence of account-wide futures/options coverage: new or omitted instruments on subscribed channels stop capture until the manifest is corrected.

## PostgreSQL gate and ownership

The schema is provisioned separately using the qualified migrations. Host never migrates/reset databases. Before opening a venue socket it rejects administrative roles, public-schema CREATE privilege, missing capture privileges and write access to the listed legacy/execution tables. The application role needs:

| Relation | Required privileges |
|---|---|
| `oms_inbox` | SELECT, INSERT, UPDATE |
| `oms_private_capture_observations` | SELECT, INSERT, UPDATE |
| `oms_integrity_incidents` | SELECT, INSERT |
| `oms_integrity_incidents_incident_id_seq` | USAGE |
| Database / public schema | CONNECT / USAGE |

UPDATE on inbox/observations is needed for `SELECT ... FOR UPDATE`; the existing immutable triggers continue to prohibit actual row mutation. DELETE/TRUNCATE on these capture relations, UPDATE on incidents, and INSERT/UPDATE/DELETE/TRUNCATE on Orders, Fills, OMS intents/commands/outbox/attempts/aggregates/account leases are refused. This is a startup privilege check for this composition, not a general database security audit or tenant RLS implementation. Deployment must preserve the qualified constraints/triggers and use a dedicated non-owner principal.

`OmsPostgresPrivateCaptureOwner` holds a separate PostgreSQL session advisory lock keyed by the complete account scope in the same database. A second cooperating capture host for that scope fails before opening a venue socket. Other scopes have distinct keys. Normal shutdown waits for the writer and releases the owned lock/connection; a new host may then acquire ownership. Npgsql pooling retains the borrowed session for the owner's lifetime; use direct PostgreSQL or session-preserving connectivity, not a transaction-pooling proxy. All cooperating replicas must use the same DB and approved unique local-to-remote account mapping.

Before each capture the owner checks its held connection. A broken session fails capture and is never silently reopened or reacquired. During an idle stream lease loss is detected on the next capture attempt; this is not an immediate network-partition detector. A lost lock can race an already-started commit on another connection, so normal scoped inbox dedup/observations remain necessary. **This read-only ownership lock is not physical-send fencing and must never authorize execution.** B5 still owns sender fencing. [PostgreSQL advisory lock semantics](https://www.postgresql.org/docs/17/explicit-locking.html#ADVISORY-LOCKS), [privilege inquiry semantics](https://www.postgresql.org/docs/17/functions-info.html#FUNCTIONS-INFO-ACCESS-TABLE).

Host connection settings force public search path, disabled error detail/parameter logging, a bounded pool of eight and bounded connection/command deadlines. Terminal DB, binding or transport failures are sanitized without inner private/DB messages, stop the host and produce a nonzero executable exit code. Retryable transport failures retain the existing bounded reconnect behavior. No background supervisor blindly restarts a terminal account/auth/store failure inside the host.

## Qualification and remaining gates

Host startup/composition, full main/subaccount binding, explicit safety switches, restricted-role real PG capture, duplicate owner refusal, normal release/reacquisition and loss of the owned test DB session have automated offline coverage. The PostgreSQL fault test terminates only the backend belonging to its generated application role and generated clone; no server/container restart is involved.

This does not qualify a real credential/account mapping, TLS to Deribit, live instrument/account coverage or recovery completeness. Every connected epoch remains RECOVERING. Next is separately recorded live read-only acceptance with approved server metadata and restricted DB role; never start it against persistent infrastructure merely to complete a test. No live host is deployed by this change. B4 snapshots/history/fills/projection and B5 risk/fencing remain required before execution.
