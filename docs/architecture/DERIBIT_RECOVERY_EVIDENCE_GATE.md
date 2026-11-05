# B4-04a — application recovery evidence gate

`DeribitRecoveryEvidenceGate` composes the existing retained reader, bounded query
port and known-order reader in Application. Connectors remain independent of
PostgreSQL/EF/Infrastructure. This is an opt-in read-side assessment, not an account
READY transition, durable publication mechanism, sender or physical fencing proof.
Durable encodings/migrations and the capture coordinator are unchanged.

## Trusted request and retained snapshot

The request pins the full scope, epoch, explicit instrument and trusted currency/
native units, alongside the UTC outage interval and positive overlap. Units and
identity are not inferred from symbols or returned rows. Invalid request bounds or
caller cancellation are refused before I/O. All I/O receives the caller token;
port exceptions propagate rather than becoming successful empty evidence.

First, the retained reader must return a usable bounded snapshot with exact scope,
valid snapshot ID and UTC timestamp. The timestamp must be within the invocation's
five-second clock tolerance; this is a stale/binding check, never a commit-order or
coherence proof. Every envelope/disposition is validated, all embedded scopes must
match, canonical unique-key/digest counts must agree with AcceptedFactCount, and
collision/unsupported evidence blocks before any private venue requests.

The known-order selector retains its existing terms/progression checks. Only the
selected instrument is assessed; other scoped retained facts are preserved. Empty
retained instrument history does not manufacture known references or readiness.

## Query evidence

The bounded query result must bind to this scope/epoch/instrument/currency and exact
expanded query interval. Its invocation/receive timestamps must be UTC and belong
to this call. Five *distinct named* sources must have valid page counts and be
exhausted; a count-only success, duplicate source names, truncated pages or source
problems block further addressed fallback.

Envelope scope/epoch, native units, receive times, method/kind provenance and query
timestamp bounds are checked independently. Open-order snapshots can predate the
outage; order-history and execution facts must be within the requested expanded
interval. Conflicting immutable payloads and contradictory simultaneous order
reports are rejected. New external order evidence remains explicitly blocking;
external IDs are never promoted into trusted retained references.

## Bounded addressed plan

For each retained reference, a compatible current order report avoids redundant
addressed I/O. Newer contradictory terms/regressing state block the query evidence.
References still lacking a current report are planned as follows:

- A retained terminal report aged at least 30 minutes adds KnownOrderRetentionExpired
  and KnownOrderUnavailable; get_order_state is not polled again outside its supported
  closed-order window. The original terminal fact stays retained.
- A nonterminal old order can still be open, so it remains eligible for an addressed
  lookup. Recent terminal references are also eligible.
- The existing selector caps known references at 32; each planned reference is read
  once, with no retry or historical-flag fallback.

Addressed results must match the same snapshot/scope/epoch and exact request count,
with a valid permutation of request indices. Found reports must cover the requested
IDs and match terms/time/cumulative/terminal constraints. Missing/unsupported results,
malformed indices, hidden partial facts and failed/misbound reads block assessment.
No original or current private facts are returned by the gate; its output contains
context, anonymous counts and typed blockers only.

## Authority and remaining evidence

Current query ports explicitly cannot establish historical unfilled coverage or full
order history; those add distinct blockers. The gate also always reports
CoherentCutUnavailable, BufferedReplayUnavailable and ExecutionLedgerCoverageUnproven:
there is no verified coherent cut, buffered replay input or complete relevant lifetime
execution ledger in this increment. Found lookups and exhausted quiet queries cannot
remove these requirements.

Therefore the current orchestrator returns `CanRebuildOrders=false`. It does not
invoke the reducer with invented complete coverage or extend the query cutoff to
receive time. `AccountReady=false` and `DurableProjectionPublished=false` remain
explicit. Positions/balances, durable projection/checkpoint publication and physical
sender fencing are additional gates, not implied by an order-only assessment.
Future positive assessment requires new independently qualified cut/replay/ledger
evidence; adding caller-supplied "complete" flags is not a replacement.

## Qualification boundary

`scripts/deribit/Deribit-RecoveryAssessmentLocal.ps1` launches `--assess-recovery`
through the existing owned retained-store runner. It uses the saved encrypted pin/
vault, committed source and SELECT-only role, validates public ETH-PERPETUAL metadata,
and runs the application gate with the real reader and query ports. It never
provisions/migrates a store, invokes the completed order harness or writes facts.

`RECOVERY_EVIDENCE_BLOCKED` with exit 0 means a diagnostic assessment completed;
it does not mean recovery success. Only counts, times, blocker names and source/
assembly hashes are saved under ignored `.tradeops/recovery-assessment/` and
`.tradeops/mainnet-recovery/`. The latter retains database count audits, Resource
Guard and container snapshots before/after. Native IDs and secrets are never printed.

Next: qualified coherent cut/buffered replay contracts tied to the active capture
scope/epoch, followed by complete execution-ledger reconciliation, positions/balances
and durable projection/recovery gates. No new trade is needed or authorized here.
