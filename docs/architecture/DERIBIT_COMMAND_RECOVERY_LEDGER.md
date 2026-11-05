# B4: durable command universe in recovery

2026-11-05. Read-side extension; no durable schema migration or change to existing
command v1 / native event v2 / capture observation v1 canonical encodings.

## Source and consistency

`IOmsRecoveryLedgerReader` returns the entire bounded command universe for an
explicit tenant/venue/environment/account/subaccount, together with retained private
facts. `OmsPostgresRecoveryLedgerReader` reads commands, intents, aggregate versions,
outbox, attempts, inbox, capture observations and integrity incidents in **one
REPEATABLE READ / READ ONLY transaction**, identified by the private snapshot ID
and database transaction timestamp. The private reader's internal transaction-bound
method is shared; the standalone private-reader port keeps its original behavior.
The snapshot is database-coherent only; it is not a venue coherent cut.

All commands are read without instrument/date filtering. Filtering to a recent
window or to orders that already have private events would hide old unresolved
dispatches. Limits are 1..10,000 commands and the existing independent private-row
limits, 16 KiB per command JSON and 16 MiB of command JSON. An overflow cannot be
returned as a usable truncated universe. Query/database failures propagate; they
never produce an empty successful ledger.

The adapter reconstructs the original canonical command, validates its full scope,
IDs, payload, schema version and SHA256, checks stored kind/version/intent/provenance,
and requires complete contiguous aggregate command versions matching the aggregate
header. Missing outbox rows, orphaned intents/versioned aggregates and mismatched
dispatch state block the result. Journal v1 permits at most one attempt, number 1;
future retry semantics require a versioned compatibility decision, not permissive
reading of additional attempts.

| Command phase | Outbox | Attempt |
|---|---|---|
| PersistedNotSent | NotSent | absent |
| DispatchStarted / Ambiguous | NeedsRecovery | one, matching phase |
| Acknowledged / Rejected | Resolved | one, matching phase |

## Gate integration and remaining gaps

`DeribitRecoveryEvidenceGate.AssessWithLedgerAsync` validates the actual combined
snapshot, freezes its collections and passes its private component to the existing
order gate. It never performs a second independent private read. An invalid command
snapshot adds `CommandLedgerInvalid` and prevents venue calls.

The account-wide command list is not narrowed to the requested instrument. Other
instruments receive `InstrumentOutsideRecoveryScope`. Unsent commands are counted
separately and receive no retry/send authority. Started/ambiguous sends receive
`UnresolvedDispatch`; sent placement commands receive `PlacementIdentityUnproven`;
acknowledged cancel/replace commands receive `MutationOutcomeUnproven`. A journal
rejection receives `RejectionNotReconciled`: a recorded transport outcome alone is
not an independently reconciled venue outcome.

Journal v1 contains no durable response/native-order binding for placements. A
client label, FIX client ID, matching quantity or quiet current query cannot supply
that missing authority. Existing explicit target IDs of cancel/replace commands
also do not prove their outcome. Therefore command issues add
`CommandRecoveryUnresolved`, even when a retained terminal order separately passes
its native fill-quantity check. The gate does not guess a command-to-order mapping.

`ExecutionLedgerCoverageUnproven` and all existing history/replay/coherence/coverage
requirements remain. An empty or internally valid local command journal cannot
exclude external/native-only orders, unseen fills or corrections. READY and durable
publication remain false. New blocker values are appended to the enum.

This entry point is qualified against owned disposable PostgreSQL clones. The live
account diagnostic still uses its existing private-only reader route and schema v2;
the mainnet reader role has no command/attempt grants. No role provisioning, grants,
migrations, trading, live re-query or persistent journal writes are part of this
change. Activation in that host requires an explicitly validated read-only store
binding and integration with account/capture lifecycle; it must not silently
escalate to the journal writer role.

Next: versioned durable native identity/outcome evidence and reconciliation of the
enumerated unresolved commands, followed by the common history/capture basis and
persisted recovered state. The [B4 to QuickFIX/n plan](../orchestration/B4_TO_QUICKFIX_PLAN_2026-11-05.md)
remains the delivery sequence.
