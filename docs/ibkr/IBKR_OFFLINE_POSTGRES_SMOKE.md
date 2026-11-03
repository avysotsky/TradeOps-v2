# TO-V2-11 — Isolated PostgreSQL smoke for IBKR offline journal

**Scope:** opt-in PostgreSQL runtime acceptance for the synthetic
`IbkrOfflinePostgresJournal` created in TO-V2-10 Draft PR #12.
This is neither a broker connection nor order execution.

## Exact command

The executable is `tools/TradeOps.IbkrOfflinePostgresSmoke` and is
part of `TradeOps.sln`, so the normal autonomous WSL2 `dotnet build`
verifies its compilation. **Building it does not run the database smoke.**

Only an already provisioned, disposable **local** PostgreSQL database
whose name starts with `tradeops_ibkr_offline_smoke_` is eligible. The
runner must supply its private connection string and explicit opt-in.
Do not paste passwords, certificates or connection strings into GitHub
or this chat.

Example *automated lab* invocation (not a request for manual owner
test handoff):

```bash
TRADEOPS_IBKR_OFFLINE_SMOKE_CONFIRM=DISPOSABLE_LOCAL_POSTGRES_ONLY \
TRADEOPS_IBKR_OFFLINE_SMOKE_PG="$PRIVATE_DISPOSABLE_LOCAL_PG_CONNECTION_STRING" \
dotnet run --project tools/TradeOps.IbkrOfflinePostgresSmoke \
    --configuration Release
```

The CLI refuses to run (exit code 2) if the opt-in is missing, the host
is not loopback, or the database name lacks the disposable-test prefix.
It creates a random `ibkr_smoke_<guid>` schema and sets the Npgsql
connection search path for all journal operations, then executes only
the opt-in TO-V2-10 schema SQL. The smoke schema is dropped in `finally`;
failure to clean up produces FAIL rather than an optimistic PASS.

## Runtime scenarios

- Persist immutable intent; reject a different quantity for same account/cOID.
- Persist preflight and `SubmissionStarted`; recover in a new journal
  instance and refuse a blind repeated submission.
- Bind synthetic broker ID to cOID and persist first partial fill.
- Replay the same execution ID without increasing filled quantity.
- Reject conflicting repeat execution ID/quantity and excessive fill.
- Verify rollback: rejected fill leaves no inserted execution row.
- Run **two concurrent PostgreSQL connections** appending distinct fills;
  verify order-level row lock prevents lost updates or double counting.
- Restart journal instance; reload exact final state and execution ledger.
- Prevent reuse of one execution ID by another local order on same account.
- Verify two synthetic intents existed, then remove the smoke schema.

Expected successful output begins:

```text
IBKR OFFLINE POSTGRES SMOKE PASS
```

The success line also includes `broker_requests=0 cleanup=true`.
No real account identifiers appear in fixtures or output.

## CI/WSL2 qualification boundary

The existing unattended WSL2 Draft PR runner still publishes
`avysotsky/TradeOps-Relay-Queue/autotest-results/tradeops/pr-<PR>/<SHA>.json`.

That JSON proves restore/build/unit tests/cleanup for the exact
commit. It **does not** prove the opt-in PostgreSQL smoke executable ran.
Database acceptance may only be recorded PASS when the trusted lab
runs this executable and captures the exit code and unredacted-sensitive-free
success marker. No additional manual relay queues, GitHub Actions,
IBKR logins or broker mutations are authorized.

This is a deterministic offline lab designed for controlled integration
testing. No production-grade PostgreSQL migration, automated recovery
worker, cross-broker adapter activation, or NuGet publishing is included.
