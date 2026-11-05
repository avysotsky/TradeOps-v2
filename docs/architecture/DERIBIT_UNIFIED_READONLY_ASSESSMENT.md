# Unified read-only B4 assessment

`--assess-account-recovery` is a dedicated opt-in entry before any order/store-write
path. It composes the production retained PostgreSQL reader, bounded venue order/
trade queries, known-order reader and scoped positions/balance reader through
`DeribitRecoveryEvidenceGate.AssessAccountAsync`. It does not publish a projection,
start capture, run a sender or change execution switches.

## Bounded scope and resource binding

The input purpose is PINNED_ACCOUNT_RECOVERY_ASSESSMENT_V1, environment Mainnet,
with the existing encrypted native account pin and saved credential. The recovery
interval must be UTC, ordered, no longer than seven days and not end in the future.
Input is limited to 16 KiB and a small JSON depth; unknown fields are refused.
Plaintext input buffers are zeroed and credential references dropped on completion.

Before private authentication or store access, the host reads the public ETH/future
catalog and builds EthActiveInverseFuturesV1. The selected order/trade query remains
**ETH-PERPETUAL only**; positions cover the explicit active inverse ETH/future group,
and balance/equity describe ETH. This is intentionally narrower than account-wide
recovery. The same scope/epoch and frozen trusted metadata feed every port.

The host opens only the existing qualification journal through `RecoveryStore.OpenReader`:
fixed owned database/cluster/native scope, existing marked SELECT-only role, no
provisioning, migrations, grants or write path. A disposable reader lease closes
the source on success, cancellation or exception. Invalid input/metadata refuses
before store access; invalid retained evidence suppresses private queries. Closed
known orders outside retention remain blocked without another addressed lookup.

Orders use a two-minute overlap, at most five pages per source and 2,000 facts.
Private queries have their existing bounds; account observations retain the trusted
30-second maximum age at assessment. The host has an overall 120-second deadline.
There are no RPC retries or automatic manifest expansion.

## Local invocation and environment evidence

`scripts/deribit/Deribit-AccountRecoveryLocal.ps1` uses the existing Windows-DPAPI
credential and native pin, verifies their fingerprint and passes plaintext only over
stdin. Source must be committed; only the owner's launchSettings may be untracked.
It records and rechecks the tool, connector, Application, Domain and Infrastructure
DLL hashes and source HEAD. Both ordinary execution switches are false.

The wrapper invokes the existing Ubuntu retained-reader runner in the exact new mode.
That runner validates the owned persistent journal/container/network/volume, uses the
restricted role, runs resource guard before/after, compares peer container lifecycles
and audits journal counts before/after. It does not start, restart, migrate or remove
the persistent server. Tool output is the dedicated diagnostics DTO, never private
assessment objects. The final environment evidence marker must report exit 0.

The wrapper accepts exactly the versioned diagnostic fields, primitive types,
constant query scopes, bounded counts, typed blockers/problems and consistent
completion/availability flags. Unknown fields, private error text, claimed READY,
missing mandatory gates and mismatched process exit are refused. Raw stderr and
plaintext input are never saved. Local reports remain under ignored `.tradeops/`.

## Meaning of the result

RECOVERY_ACCOUNT_EVIDENCE_BLOCKED with AssessmentCompleted=true and exit 0 means
the **diagnostic pipeline completed**, with explicit unresolved recovery blockers.
It does not mean order evidence or the account was recovered. Refusal has a separate
status and nonzero exit; store-attempt/SELECT-validation flags remain distinct.

Only counts, typed reasons and local times leave the host. No native account ID,
returned instrument names, order IDs, sizes, balances or credentials are serialized.
The five baseline blockers remain: CoherentCutUnavailable, BufferedReplayUnavailable,
ExecutionLedgerCoverageUnproven, AccountSnapshotCoherenceUnproven and
AccountWideCoverageUnproven. History, retention and source-specific problems add
further blockers. AccountWideCoverage, VenueCoherent, AccountReady,
DurableProjectionPublished and TradingReady stay false.

This plain entry has no active capture/replay session. It cannot replace the active
assessment API's ownership proof or establish a common venue cut. The remaining B4
delivery work is complete supported coverage/coherence, persisted recovered state
and an end-to-end readiness/failure qualification; then mandatory risk/sender gates
and Deribit QuickFIX/n qualification follow the platform roadmap.
