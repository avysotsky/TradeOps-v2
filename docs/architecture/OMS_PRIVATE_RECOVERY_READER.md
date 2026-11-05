# B4-02 — scoped durable private recovery reader

`IOmsPrivateRecoveryReader` is an application read port. The PostgreSQL adapter
`OmsPostgresPrivateRecoveryReader` reads the existing inbox, capture observations
and integrity incidents for exactly one tenant/venue/environment/account/subaccount.
Only the existing Deribit testnet/mainnet private v2 facts and v1 observations are
supported. Unsupported legacy/future rows block this reader; no old data is changed.
There is no migration, sender, exchange request or automatic host registration.

## Snapshot and bounds

Every invocation creates one REPEATABLE READ / READ ONLY transaction. It reads the
accepted facts, all observation dispositions and existence of any scoped integrity
incident from the same database snapshot. A SELECT-only ordinary role suffices.
The result retains the full scope, a fresh snapshot ID and the database UTC read
time. Snapshot ID/read time are diagnostic context, not durable resume positions.

The existing recorded timestamps and incident sequence IDs do not establish commit
ordering. A transaction may allocate/record before another one and commit later.
This implementation therefore rereads the complete bounded scope on each call;
it does not save or advance a timestamp/identity checkpoint. A future scalable
checkpoint needs a separate commit-safe design, with its own version and evidence.

Limits: 1–10,000 rows per inbox/observation set, 128 KiB SQL JSON-text fetch per row,
16 MiB aggregate fetched JSON. LIMIT+1 detects truncation; SQL suppresses oversized
JSON bodies before returning them. Exceeding any bound returns an unusable snapshot
and no observations, rather than silently losing history. Future account/order
partitioning must preserve the entire selected execution ledger and its incidents.

## Validation and blocking

- Require supported DB/payload schema versions and exact field sets. Decode with
  case-sensitive property names and unknown-member rejection, retaining explicit
  nullable fields. JSONB formatting is not hashed as text: reconstruct the existing
  canonical encoding and verify its SHA-256 instead.
- Compare embedded scope to the trusted query scope; verify capture ID, stream
  epoch, event namespace/ID and observation SHA against stored columns.
- Match every accepted/duplicate observation to a same-snapshot accepted inbox
  fact, kind and canonical digest. Every accepted fact needs a validated accepted
  observation. Unsupported/malformed/missing/orphaned data clears returned evidence
  and blocks use. Transport/database errors and cancellation propagate.
- Preserve valid quarantined alternative observations for diagnosis. Quarantine
  and any scoped integrity incident independently block use, including incidents
  without an observation (command/intent collisions). No incident is auto-resolved.

`DeribitDurableOrderRecovery` composes the reader and the B4-01 reducer. It rejects
wrong snapshot bindings and hidden/invalid dispositions, and returns no projection
for an unusable snapshot. A usable snapshot is split by the caller's current epoch
and passed to the existing scope/window/coverage and execution-ledger comparison.
The result is not an account READY transition or order-send permission.

## Evidence and remaining scope

The `b4-reader` suite selects 87 cases: 47 prior recovery cases (the two PG replay
cases now use the production reader), 15 new composition cases and 25 reader PG
cases. They cover canonical corruption, foreign scopes, missing/unsupported data,
quarantine/incidents, limits, SELECT-only privileges, actual transaction settings,
an in-flight atomic capture and a later read of a backdated committed observation.
Corruption/RLS/locking injection stays inside owned disposable test clones.

Production DI/reader-role provisioning, live store qualification, incremental
checkpoint/projection publication, paginated venue queries, positions/balances,
coverage/coherence orchestration and physical sender fencing remain separate work.
No new mainnet trade or credential operation is performed for this increment.
