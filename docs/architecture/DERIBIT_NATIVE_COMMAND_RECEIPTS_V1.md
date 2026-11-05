# B4: native command receipts v1

2026-11-05. Additive durable format and table; command v1, private event v2 and
capture observation v1 encodings/hashes remain unchanged. No historical label/ACK
backfill and no automatic migration or grants on the retained mainnet store.

## Evidence and producer boundary

`DeribitJsonRpcCommandReceipt` retains the original immutable journal command,
attempt number, transport session UUID, numeric RPC request ID/method, local
send/receive timestamps, trusted instrument kind, normalized native order report
and echoed order type/TIF/post-only/reduce-only terms. Schema version is 1.

The pure connector parser consumes a **direct correlated JSON-RPC success response**:
placement reads `result.order`, addressed cancel reads `result`. It rejects errors,
wrong/missing IDs, duplicate JSON fields, missing cumulative/terms and mismatched
native values. The shapes are based on the official
[private/buy](https://docs.deribit.com/api-reference/trading/private-buy) and
[private/cancel](https://docs.deribit.com/api-reference/trading/private-cancel)
response contracts, checked 2026-11-05. Inline trades are not turned into capture
events, executions or a completeness claim.

The producer must be the trusted order-entry path on an authenticated session whose
native account binding is independently verified. Parser inputs are not network
authentication; neither the session UUID nor a SHA256 authenticates the venue.
The parser has no network/database access and does not send. Arbitrary lookup
results, labels, matching quantities, a caller's `complete=true`, and manually
guessed native IDs cannot be used as a substitute producer.
If the venue response was never received, no direct receipt can be manufactured.
That case remains unresolved and needs separately qualified recovery evidence;
the lost-ACK replay tests here concern an already committed database receipt.

Version 1 supports only initial Place (expected aggregate version 0) and addressed
Cancel for unmodified BTC/ETH inverse futures, native USD `QuoteAsset` quantity,
numeric limit/GTC terms, attempt 1. Explicit trusted metadata supplies currency
and native units. No symbol-based units conversion is performed. Options, linear/
spot, trigger orders, replace, cancel-by-label, RPC errors without an order and FIX
remain unsupported. FIX will need its own protocol evidence/correlation contract.

The original command, full account scope, side, amount, price and echoed flags must
match. Timestamps are UTC, the observed response interval is at most one minute;
venue/DB clock comparisons have the explicit five-second tolerance. A replay of
already observed evidence may be recorded later; it does not grant resend authority.

## Persistence and compatibility

`OmsPostgresDeribitCommandReceiptJournal` writes only new receipt evidence and
collision incidents. It does not change dispatch phase, attempts, outbox, capture
rows, Orders/Fills or readiness. Commands must already have a durable attempt in
DispatchStarted/Ambiguous/Acknowledged with matching outbox state. A phase-limited
locked read binds the receipt to actual immutable command JSON/hash and attempt
chronology before insert.

The new `oms_deribit_command_receipts_v1` table references the scoped attempt and
aggregate. A command has one receipt; session/request IDs are unique in full scope;
one native currency/order ID cannot be claimed by different placements. An
account-scoped transaction advisory lock arbitrates receipt collisions only. It
is not sender ownership/fencing. Identical replay returns Duplicate; a different
receipt on the same command, native placement identity or RPC request context
preserves the first receipt and records an integrity incident atomically.
Incidents retain the existing `CommandCollision` category with a
`deribit-receipt-v1:<command-id>` correlation; the incident table's enum/check
contract is not widened or reinterpreted as a new schema.

An addressed Cancel also requires a valid earlier placement receipt of the same
aggregate/native order, with identical native terms and flags. It cannot precede
the placement response, regress native timestamp/cumulative fill, or cancel an
already terminal placement response. Contradictions are refused without repair.

Receipt canonical decimal encoding normalizes numeric scale. Original journal v1
encoding/hash is checked with its original decimal scale and never rewritten.
Reader decoding reconstructs the exact supported shape and re-encodes for hash
verification; unknown versions, omitted/extra fields or foreign bindings fail.

Update/delete/truncate are forbidden by triggers, including for the fixture owner;
nonempty migration rollback refuses destruction. Migration is applied only to
owned disposable test clones in this qualification. Permanent databases and roles
are unchanged. A future deployment must explicitly qualify its migration/store
binding and least-privilege grants. Capture ownership now refuses receipt-write
privileges in addition to existing command/projection authority.

## Recovery integration

The combined ledger reader includes receipts in the **same REPEATABLE READ / READ
ONLY transaction** as commands, attempts, outbox and private facts. It validates
column/JSON/hash/command/attempt bindings, entire receipt-set uniqueness and Cancel
ancestry. Original command row limits remain; receipts have the same bounded row
limit, 32 KiB JSON each and share the 16 MiB command/receipt batch budget. No usable
partial universe is returned on overflow/corruption. Missing table/privileges is
a database failure, not an empty successful result. Legacy snapshots with no
receipts keep their unresolved command problems.

`AssessWithLedgerAsync` freezes and validates actual receipt evidence before venue
IO. `BoundCommands` counts valid durable direct-response bindings. A matching
accepted native report at least as recent as the receipt must also exist in the
private snapshot. Report scope/terms/cumulative/terminal state must agree;
execution leg price/amount are not confused with order terms. Missing or conflicting
private evidence yields BoundOrderEvidenceUnavailable/BoundOrderEvidenceMismatch.
The receipt is not inserted as a synthetic WS/recovery observation.

A valid binding plus matching private evidence removes only that command's
UnresolvedDispatch/PlacementIdentityUnproven/MutationOutcomeUnproven issues. It does
not repair persisted command/outbox phases. Other commands, unsupported outcomes,
historical gaps and external orders remain unresolved. History/fill ledger/replay/
venue cut/account coherence and coverage requirements remain; READY/publication
stay false. A cancel report/ACK is not evidence of zero fills.

## Current delivery boundary

Parser, journal, migration, combined reader and Application gate are implemented.
The existing consumed one-off mainnet harness is unchanged and does not produce
these receipts. There is no backfill or new live qualification. The next host must
retain original per-call request/session context and write the actual parsed direct
response through this port; mainnet role/migration activation must be qualified
separately. Any real order scenario is launched by the owner under the
[approved mainnet bounds](../testing/OMS_MAINNET_QUALIFICATION_POLICY_2026-11-05.md).

Next: connect the bounded owner-launched OMS scenario to receipt/capture/recovery
lifecycle and qualify read-only access to the retained store; then common history/
capture basis, persisted recovered state and full B4 lifecycle according to the
[B4 → QuickFIX/n plan](../orchestration/B4_TO_QUICKFIX_PLAN_2026-11-05.md).
