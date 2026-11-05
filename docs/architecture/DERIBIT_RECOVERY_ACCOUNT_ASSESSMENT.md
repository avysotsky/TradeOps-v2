# B4-05d — scoped account observations in Application assessment

The new explicit `AssessAccountAsync` and `AssessAccountWithReplayAsync` entries
compose the existing order-evidence gate with `IDeribitAccountSnapshotReader`.
Application depends only on its ports and Domain; it does not construct a connector,
database or qualification host. Existing order-only entries and live hosts retain
their behavior. There is no automatic venue startup, new persistence or trading path.

## Trusted request and scope

`DeribitRecoveryAccountRequest` supplies the scoped account query and server-owned
instrument expectations from validated metadata. Expectations specify native names,
size currency and base currency; they cannot be derived from returned private rows.
The complete bounded list is copied before any await or replay-window change.

Account scope and epoch must equal the order query, including tenant, environment
and native account/subaccount mapping. Currency must match the gate and be BTC/ETH;
kind must be future/option. Futures require native USD size, options require matching
base-coin size; the selected order instrument must be in the expected manifest.
Expected instruments and position capacity are bounded to 1–256, and the maximum
observation span to 1–60 seconds. Duplicate/empty/invalid expectations refuse before
any retained/account read or replay-window replacement.

The caller remains responsible for constructing those expectations from trusted
public metadata. The assessment accepts no anonymous qualification report,
MetadataValidated flag, caller-supplied completeness flag or copied replay batch.
Epoch correlation in a plain read is not proof of an active capture generation.

## Lifecycle

The plain entry assesses order evidence, then reads the account port once. The active
entry reuses the existing owned begin/seal/drain/final-validation/cleanup path, with
the additional account read **after order assessment and before seal**. Thus capture
receipts overlapping the account RPCs are inside the same local replay window.
Port/caller deadlines govern account I/O; the existing bounded drain budget starts
after that read. Local replay expiry can still block a slow overall assessment.

Unusable or wrongly bound retained snapshots and invalid retained/known selection
evidence skip the account read. Other existing order blockers are preserved. Caller
cancellation and unexpected port exceptions propagate; active replay always abandons
only its own window, without deleting a newer superseding window or cancelling an
independent durable commit. There is no automatic account retry.

The raw port collections are bounded and copied immediately on return, before an
independent replay drain can await pending commits. Records contain immutable scalar
values; the caller/port cannot later change the accepted manifest or positions by
mutating its original lists. The private values remain local and are not returned by
the assessment. The nested existing evidence still contains scoped account identity
and is not an approved public diagnostics payload.

## Validation at consumption

Final validation runs after drain and opaque cut revalidation, at the time the
combined assessment is constructed. It verifies:

- Exact result scope/epoch/currency/kind and requested observation-span binding.
- Exact manifest membership/cardinality, no duplicate requested names, bounded and
  unique positions, known native instruments, expected kind and currencies, signed
  direction consistency, futures base size present and options base size absent.
- UTC local start/completion inside this invocation's read/return bounds. Cached
  results from before invocation, future completion and backward clocks are refused.
- Balance currency, UTC request/receive windows inside the result's overall bounds,
  and sequential balance/positions windows. No common venue timestamp is invented.
- Age from the raw read's start against the trusted maximum **at final use**, not a
  saved `IsFreshAt`/FreshAtAssessment value. A valid local replay can outlive the raw
  account observations; that account result is then stale and discarded.

Nonempty/unknown reader problems, missing windows/balance and malformed collections
fail closed. Negative cash/equity values are valid observations and are not treated
as buying power. An empty scoped positions array can be accepted; it does not create
zero-position facts. If active replay reports stale session/epoch or a stopped
coordinator, account acceptance is also discarded. Other replay failures such as lost
ACK do not erase independently valid scoped RPC observations, but still block recovery.

## Authority and output

Only fully valid scoped observations set ScopedAccountObservationsValidated=true
and supply accepted-row/balance-observed counts. All account failures report zero
accepted rows and BalanceObserved=false with typed problems/blockers. No amounts,
position names, private payloads or reusable freshness authority escape this result.

AccountObservationsUnavailable is omitted only for valid scoped observations.
AccountSnapshotCoherenceUnproven and AccountWideCoverageUnproven are always added.
Every existing order/history/ledger/replay blocker remains, with the existing replay
entry removing only BufferedReplayUnavailable for a valid local prefix. Local replay
does not reconcile positions/balances against venue history or the execution ledger.
AccountWideCoverage, VenueCoherent, AccountReady and DurableProjectionPublished stay
false. Existing blocker numeric values are preserved by appending new diagnostics;
durable contracts, canonical encodings and DB schemas are unchanged.

Qualification is offline/DB-only. Scripted ports validate failures and lifecycle;
actual connector account reads use scripted HTTP while retained reader/capture
integrations use owned PostgreSQL clones. Mainnet state and the saved vault are not
used by this increment. A separate host binding/qualification remains necessary.
